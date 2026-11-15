using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    /// <summary>
    /// Render extensions supply geometry/material commands; MaxSRP owns
    /// camera matrices, render targets and pass order. Extension packages depend
    /// on this contract; the pipeline has no dependency on their implementations.
    /// </summary>
    public interface IMaxRenderParticipant
    {
        bool RendersCamera(Camera camera);
        void PrepareCamera(ScriptableRenderContext context, Camera camera);
        void RecordGBuffer(CommandBuffer commands, Camera camera);
        void RecordMotion(CommandBuffer commands, Camera camera);
        void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 shadowView,
            Matrix4x4 shadowProjection, Plane[] shadowPlanes);
        void EndCamera(ScriptableRenderContext context, Camera camera);
    }

    // Optional editor/debug geometry is drawn into scene color after temporal
    // resolve. Wire colors must never overwrite packed GBuffer attachments.
    public interface IMaxRenderOverlayParticipant
    {
        void RecordOverlay(CommandBuffer commands, Camera camera);
    }

    // Optional per-camera material diagnostics use the host's existing linear
    // mask output path, with no photographic exposure, tone mapping or Bloom.
    public interface IMaxRenderDiagnosticParticipant
    {
        bool UsesMaterialDiagnostics { get; }
    }

    public static class MaxRenderBridge
    {
        private static readonly List<IMaxRenderParticipant> s_participants =
            new List<IMaxRenderParticipant>();

        // The host subscribes while its shadow pass is alive. A terrain reset or
        // input/geometry change must retire cached far-cascade depth as well.
        public static event Action<Camera> ShadowInvalidated;

        // Explicit terrain camera cuts use the host's existing TAA / exposure /
        // shadow reset. The separate assembly does not own another history.
        public static event Action<Camera> TemporalHistoryInvalidated;

        public static void Register(IMaxRenderParticipant participant)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (s_participants.Contains(participant)) return;
            s_participants.Add(participant);
            InvalidateShadows(null);
        }

        public static void Unregister(IMaxRenderParticipant participant)
        {
            if (s_participants.Remove(participant)) InvalidateShadows(null);
        }

        public static void InvalidateShadows(Camera camera) => ShadowInvalidated?.Invoke(camera);

        public static void InvalidateTemporalHistory(Camera camera) => TemporalHistoryInvalidated?.Invoke(camera);

        public static bool HasParticipants(Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) return true;
            return false;
        }

        public static bool HasMaterialDiagnostics(Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera) &&
                    s_participants[i] is IMaxRenderDiagnosticParticipant diagnostic && diagnostic.UsesMaterialDiagnostics)
                    return true;
            return false;
        }

        public static void PrepareCamera(ScriptableRenderContext context, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].PrepareCamera(context, camera);
        }

        public static void RecordGBuffer(CommandBuffer commands, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].RecordGBuffer(commands, camera);
        }

        public static void RecordMotion(CommandBuffer commands, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].RecordMotion(commands, camera);
        }

        public static void RecordOverlay(CommandBuffer commands, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera) &&
                    s_participants[i] is IMaxRenderOverlayParticipant overlay)
                    overlay.RecordOverlay(commands, camera);
        }

        // shadowProjection is already the GPU projection for a texture target;
        // shadowPlanes are the six actual caster planes, including cascade guard.
        public static void RecordShadow(CommandBuffer commands, Camera camera, Matrix4x4 shadowView,
            Matrix4x4 shadowProjection, Plane[] shadowPlanes)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera))
                    s_participants[i].RecordShadow(commands, camera, shadowView, shadowProjection, shadowPlanes);
        }

        // Called before this camera's context.Submit. Queue feedback compaction /
        // readback through this same context so they follow the terrain MRT draw.
        public static void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            for (int i = 0; i < s_participants.Count; ++i)
                if (s_participants[i].RendersCamera(camera)) s_participants[i].EndCamera(context, camera);
        }
    }
}
