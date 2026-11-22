using Unity.Profiling;

namespace MaxSRP
{
    // CPU samples for the render pipeline, including the cost of submitting its commands.
    internal static class MaxRenderProfiler
    {
        internal static readonly ProfilerMarker RenderFrame = new ProfilerMarker("MaxSRP.RenderFrame");
        internal static readonly ProfilerMarker FrameSetup = new ProfilerMarker("MaxSRP.FrameSetup");
        internal static readonly ProfilerMarker GameCamera = new ProfilerMarker("MaxSRP.Camera.Game");
        internal static readonly ProfilerMarker SceneViewCamera = new ProfilerMarker("MaxSRP.Camera.SceneView");
        internal static readonly ProfilerMarker OtherCamera = new ProfilerMarker("MaxSRP.Camera.Other");
        internal static readonly ProfilerMarker TemporalHistory = new ProfilerMarker("MaxSRP.Camera.TemporalHistory");
        internal static readonly ProfilerMarker CullingSetup = new ProfilerMarker("MaxSRP.Camera.CullingSetup");
        internal static readonly ProfilerMarker Cull = new ProfilerMarker("MaxSRP.Camera.Cull");
        internal static readonly ProfilerMarker PrepareParticipants = new ProfilerMarker("MaxSRP.Camera.PrepareParticipants");
        internal static readonly ProfilerMarker LightSetup = new ProfilerMarker("MaxSRP.Camera.LightSetup");
        internal static readonly ProfilerMarker Shadows = new ProfilerMarker("MaxSRP.Camera.Shadows");
        internal static readonly ProfilerMarker EnsureRenderTargets = new ProfilerMarker("MaxSRP.Camera.EnsureRenderTargets");
        internal static readonly ProfilerMarker GBufferSetup = new ProfilerMarker("MaxSRP.Camera.GBufferSetup");
        internal static readonly ProfilerMarker DrawOpaque = new ProfilerMarker("MaxSRP.Camera.DrawOpaque");
        internal static readonly ProfilerMarker ParticipantGBuffer = new ProfilerMarker("MaxSRP.Camera.ParticipantGBuffer");
        internal static readonly ProfilerMarker MotionVectors = new ProfilerMarker("MaxSRP.Camera.MotionVectors");
        internal static readonly ProfilerMarker Atmosphere = new ProfilerMarker("MaxSRP.Camera.Atmosphere");
        internal static readonly ProfilerMarker Clouds = new ProfilerMarker("MaxSRP.Camera.Clouds");
        internal static readonly ProfilerMarker SkyIBL = new ProfilerMarker("MaxSRP.Camera.SkyIBL");
        internal static readonly ProfilerMarker ClusteredLights = new ProfilerMarker("MaxSRP.Camera.ClusteredLights");
        internal static readonly ProfilerMarker AmbientOcclusion = new ProfilerMarker("MaxSRP.Camera.AmbientOcclusion");
        internal static readonly ProfilerMarker DeferredLighting = new ProfilerMarker("MaxSRP.Camera.DeferredLighting");
        internal static readonly ProfilerMarker DrawSky = new ProfilerMarker("MaxSRP.Camera.DrawSky");
        internal static readonly ProfilerMarker TemporalResolve = new ProfilerMarker("MaxSRP.Camera.TemporalResolve");
        internal static readonly ProfilerMarker PostProcess = new ProfilerMarker("MaxSRP.Camera.PostProcess");
        internal static readonly ProfilerMarker EditorGizmos = new ProfilerMarker("MaxSRP.Camera.EditorGizmos");
        internal static readonly ProfilerMarker ColorOutput = new ProfilerMarker("MaxSRP.Camera.ColorOutput");
        internal static readonly ProfilerMarker EndParticipants = new ProfilerMarker("MaxSRP.Camera.EndParticipants");
        internal static readonly ProfilerMarker ContextSubmit = new ProfilerMarker("MaxSRP.Camera.ContextSubmit");
    }
}
