using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // GrassFinal's approved GotGrass contact and moving-particle recipe.
    // Update owns the frame clock. The first terrain view records the shared world-space map.
    internal sealed class GotGrassDisplacement : IDisposable
    {
        const int MapSize = 256;
        const float MapWidth = 100f;
        const float TexelSize = MapWidth / MapSize;
        const int MaxColliders = 32;
        const int TrailCapacity = 1024;
        const int StampCapacity = MaxColliders + TrailCapacity;
        const int TicksPerSecond = 1440;

        static readonly AnimationCurve DefaultEnvelope = GotGrassInteractionSettings.CreateEnvelope();
        static readonly int MapId = Shader.PropertyToID("_GotGrassDisplacementMap");
        static readonly int WindowId = Shader.PropertyToID("_GotGrassDisplacementWindow");
        static readonly int BoundsId = Shader.PropertyToID("_GotGrassDisplacementBounds");

        struct Stamp
        {
            public Vector2 Center, Axis;
            public float HalfWidth, HalfLength, Strength, FootHeight;
        }

        struct Emitter
        {
            public int ColliderId, Frame;
            public float DistanceToTrail;
            public Stamp Stamp;
        }

        struct Trail
        {
            public Stamp Stamp;
            public int BirthTick;
            public float Lifetime, Frequency, Speed, Size;
        }

        readonly GotGrassInteractionSettings settings;
        readonly Emitter[] emitters = new Emitter[MaxColliders * 2];
        readonly int[] colliderIds = new int[MaxColliders];
        readonly Trail[] trails = new Trail[TrailCapacity];
        readonly Vector4[] stampCenters = new Vector4[StampCapacity];
        readonly Vector4[] stampAxes = new Vector4[StampCapacity];
        readonly MaterialPropertyBlock stampProperties = new MaterialPropertyBlock();

        RenderTexture displacementMap;
        Material stampMaterial;
        ComputeBuffer stampCenterBuffer, stampAxisBuffer;
        Vector2 mapCenter;
        Vector4 stampBounds;
        int emitterCount, trailCount, stampCount;
        int lastUpdatedFrame = int.MinValue;
        double elapsedTime;
        uint random = 1;
        bool pendingRecord, hasDisplacement;

        public int GpuBytes => displacementMap ? MapSize * MapSize * 4 + StampCapacity * 32 : 0;

        public GotGrassDisplacement(Shader shader, GotGrassInteractionSettings settings)
        {
            this.settings = settings;
            if (!shader || !settings || !SystemInfo.supportsComputeShaders) return;
            const GraphicsFormat format = GraphicsFormat.R16G16_SFloat;
            if (!SystemInfo.IsFormatSupported(format, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(format, FormatUsage.Blend) ||
                !SystemInfo.IsFormatSupported(format, FormatUsage.Sample))
            {
                Debug.LogWarning("GotGrass interaction requires a blendable, sampleable RG16F render target.");
                return;
            }

            try
            {
                displacementMap = new RenderTexture(new RenderTextureDescriptor(MapSize, MapSize, format, 0))
                {
                    name = "GotGrass/CurrentDisplacement",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!displacementMap.Create())
                {
                    Debug.LogWarning("Could not allocate the GotGrass displacement map.");
                    Dispose();
                    return;
                }

                stampMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                stampCenterBuffer = new ComputeBuffer(StampCapacity, 16) { name = "GotGrass/StampCenterSize" };
                stampAxisBuffer = new ComputeBuffer(StampCapacity, 16) { name = "GotGrass/StampBasisAmplitude" };
                stampProperties.SetBuffer("_GotGrassStampCenterSize", stampCenterBuffer);
                stampProperties.SetBuffer("_GotGrassStampBasisAmplitude", stampAxisBuffer);
                pendingRecord = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Update(MeshHeightSource source, Camera anchor, Collider[] colliders)
        {
            int frame = Time.frameCount;
            if (!Application.isPlaying || !displacementMap || lastUpdatedFrame == frame) return;
            lastUpdatedFrame = frame;
            stampCount = 0;
            if (source != null && anchor && settings)
            {
                float deltaTime = Time.deltaTime;
                elapsedTime += deltaTime;
                var position = anchor.transform.position;
                var forward = anchor.transform.forward;
                Vector2 desired = new Vector2(position.x, position.z) +
                    new Vector2(forward.x, forward.z).normalized * 40;
                Vector2 steps = (desired - mapCenter) / TexelSize;
                mapCenter += new Vector2((int)steps.x, (int)steps.y) * TexelSize;
                UpdateColliders(source, colliders, frame, deltaTime);
                UpdateTrails();
            }
            else
            {
                emitterCount = trailCount = 0;
            }

            pendingRecord |= stampCount > 0 || hasDisplacement;
        }

        void UpdateColliders(MeshHeightSource source, Collider[] colliders, int frame, float deltaTime)
        {
            int colliderCount = 0;
            foreach (Collider collider in colliders ?? Array.Empty<Collider>())
            {
                if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (colliderCount == MaxColliders) break;
                int id = collider.GetInstanceID();
                if (Array.IndexOf(colliderIds, id, 0, colliderCount) >= 0) continue;
                colliderIds[colliderCount++] = id;

                GetColliderShape(collider, out Vector3 a, out Vector3 b, out float radius);
                if (!TryGetStamp(source, a, b, radius, out Stamp stamp)) continue;
                UpdateEmitter(id, stamp, source, frame, deltaTime);
                if (settings.UseColliderContact)
                    AddStamp(stamp, settings.ContactStrength * stamp.Strength, false);
            }

            for (int i = emitterCount - 1; i >= 0; i--)
            {
                if (emitters[i].Frame == frame) continue;
                ReleaseEmitter(emitters[i].Stamp);
                emitters[i] = emitters[--emitterCount];
            }
        }

        static void GetColliderShape(Collider collider, out Vector3 a, out Vector3 b, out float radius)
        {
            if (collider is SphereCollider sphere)
            {
                var transform = sphere.transform;
                Vector3 scale = transform.lossyScale;
                a = b = transform.TransformPoint(sphere.center);
                radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                return;
            }

            if (collider is CapsuleCollider capsule)
            {
                var transform = capsule.transform;
                int direction = capsule.direction;
                Vector3 scale = transform.lossyScale;
                Vector3 axis = direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward;
                float axialScale = Mathf.Abs(scale[direction]);
                float radialScale = Mathf.Max(Mathf.Abs(scale[(direction + 1) % 3]), Mathf.Abs(scale[(direction + 2) % 3]));
                radius = capsule.radius * radialScale;
                float half = Mathf.Max(0, capsule.height * axialScale * .5f - radius);
                Vector3 center = transform.TransformPoint(capsule.center);
                axis = transform.TransformDirection(axis).normalized;
                a = center - axis * half;
                b = center + axis * half;
                return;
            }

            Bounds bounds = collider.bounds;
            a = b = bounds.center;
            radius = bounds.extents.magnitude;
        }

        static bool TryGetStamp(MeshHeightSource source, Vector3 a, Vector3 b, float radius, out Stamp stamp)
        {
            stamp = default;
            if (radius <= 0) return false;
            Vector3 lower = a.y <= b.y ? a : b;
            Vector3 upper = a.y <= b.y ? b : a;
            Vector2 center = new Vector2(lower.x, lower.z);
            float footHeight = lower.y - radius;
            float contact = GroundWeight(source, center, footHeight);
            if (contact <= 0) return false;

            // Keep the low portion of a tilted capsule and the whole footprint of a horizontal one.
            Vector3 span = upper - lower;
            if (span.y > .65f) span *= .65f / span.y;
            Vector2 axis = new Vector2(span.x, span.z);
            float length = axis.magnitude;
            float width = Mathf.Max(2 * TexelSize, radius + .3f);
            stamp = new Stamp
            {
                Center = center + axis * .5f,
                Axis = length > .00001f ? axis / length : Vector2.right,
                HalfWidth = width,
                HalfLength = width + length * .5f,
                Strength = contact,
                FootHeight = footHeight
            };
            return true;
        }

        static float GroundWeight(MeshHeightSource source, Vector2 center, float height) =>
            Mathf.Clamp01((source.SampleMeters(center.x, center.y, 0) + 1 - height) * 1.25f);

        void UpdateEmitter(int id, Stamp stamp, MeshHeightSource source, int frame, float deltaTime)
        {
            for (int i = 0; i < emitterCount; i++)
            {
                ref var emitter = ref emitters[i];
                if (emitter.ColliderId != id) continue;
                AdvanceEmitter(ref emitter, stamp, source, deltaTime);
                emitter.Frame = frame;
                emitter.Stamp = stamp;
                return;
            }

            emitters[emitterCount++] = new Emitter { ColliderId = id, Frame = frame, Stamp = stamp };
        }

        void AdvanceEmitter(ref Emitter emitter, Stamp stamp, MeshHeightSource source, float deltaTime)
        {
            Stamp previous = emitter.Stamp;
            Vector2 delta = stamp.Center - previous.Center;
            float distance = delta.magnitude;
            if (distance > Mathf.Max(2, previous.HalfWidth + stamp.HalfWidth))
            {
                ReleaseEmitter(previous);
                emitter.DistanceToTrail = 0;
                return;
            }

            if (distance <= .00001f) return;
            float speed = distance / Mathf.Max(.0001f, deltaTime);
            Vector2 travelDirection = delta / distance;
            Vector2 rasterOffset = travelDirection * (settings.SpawnForwardOffset * Mathf.Clamp01(speed / 5.5f));
            float spacing = Mathf.Max(.1f, settings.SpawnSpacing);
            float path = Mathf.Min(spacing, emitter.DistanceToTrail);
            while (path <= distance)
            {
                float t = path / distance;
                Stamp trail = stamp;
                Vector2 center = Vector2.Lerp(previous.Center, stamp.Center, t);
                float footHeight = Mathf.Lerp(previous.FootHeight, stamp.FootHeight, t);
                trail.Strength = GroundWeight(source, center, footHeight);
                trail.Center = center + rasterOffset;
                trail.Axis = travelDirection;
                AddTrail(new Trail
                {
                    Stamp = trail,
                    BirthTick = ToTick(elapsedTime - deltaTime * (1 - t)),
                    Speed = speed,
                    Size = SampleRange(settings.SizeVariationRange),
                    Lifetime = SampleRange(settings.LifetimeRange),
                    Frequency = SampleRange(settings.FrequencyRange)
                });
                path += spacing;
            }
            emitter.DistanceToTrail = path - distance;
        }

        void UpdateTrails()
        {
            int kept = 0;
            int tick = ToTick(elapsedTime);
            var envelope = settings.Envelope ?? DefaultEnvelope;
            for (int i = 0; i < trailCount; i++)
            {
                var trail = trails[i];
                float seconds = (tick - trail.BirthTick) * (1f / TicksPerSecond);
                if (seconds >= trail.Lifetime) continue;
                trails[kept++] = trail;
                float age = seconds / trail.Lifetime;
                if (trail.Frequency <= 0)
                {
                    if (settings.UseColliderContact)
                        AddStamp(trail.Stamp, trail.Stamp.Strength * DefaultEnvelope.Evaluate(
                            Mathf.Lerp(GotGrassInteractionSettings.EnvelopePeakAge, 1, age)), false);
                    continue;
                }

                float sizeGain = .5f + .5f * Ramp(settings.SizeSpeedRange, trail.Speed);
                var stamp = trail.Stamp;
                stamp.HalfWidth = stamp.HalfLength = .5f * Mathf.Max(.1f, settings.QuadWidth) * trail.Size * sizeGain;
                float wave = .5f * (1 + Mathf.Sin(seconds * trail.Frequency));
                float amplitude = envelope.Evaluate(age) * 3 * Ramp(settings.AmplitudeSpeedRange, trail.Speed) *
                    Mathf.Lerp(1, wave, settings.ReboundAmount) * settings.Strength;
                AddStamp(stamp, stamp.Strength * amplitude, true);
            }
            trailCount = kept;
        }

        static int ToTick(double seconds) => (int)(seconds * TicksPerSecond);

        static float Ramp(Vector2 range, float value)
        {
            float lo = Mathf.Max(0, Mathf.Min(range.x, range.y));
            float hi = Mathf.Max(lo, Mathf.Max(range.x, range.y));
            return Mathf.Clamp01((value - lo) / Mathf.Max(.0001f, hi - lo));
        }

        float SampleRange(Vector2 range)
        {
            random = unchecked(random * 1664525u + 1013904223u);
            float weight = (random >> 8) * (1f / 16777216);
            return Mathf.Max(.1f, Mathf.Lerp(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y), weight));
        }

        void ReleaseEmitter(Stamp stamp)
        {
            if (!settings.UseColliderContact) return;
            stamp.Strength *= settings.ContactStrength;
            if (stamp.Strength <= 0) return;
            AddTrail(new Trail
            {
                Stamp = stamp,
                BirthTick = ToTick(elapsedTime),
                Lifetime = Mathf.Max(.05f, settings.ContactRecoverySeconds)
            });
        }

        void AddTrail(Trail trail)
        {
            if (trailCount == TrailCapacity)
            {
                Array.Copy(trails, 1, trails, 0, TrailCapacity - 1);
                trailCount--;
            }
            trails[trailCount++] = trail;
        }

        void AddStamp(Stamp stamp, float strength, bool travelling)
        {
            strength = Mathf.Floor(Mathf.Clamp(strength * 8192, 0, 65535)) * (1f / 8192);
            if (strength <= 0) return;
            Vector2 delta = stamp.Center - mapCenter;
            float radius = Mathf.Max(stamp.HalfWidth, stamp.HalfLength);
            float reach = MapWidth * .5f + radius;
            if (Mathf.Abs(delta.x) > reach || Mathf.Abs(delta.y) > reach) return;

            int index = stampCount++;
            float padding = radius + TexelSize * 2;
            Vector2 lo = stamp.Center - Vector2.one * padding;
            Vector2 hi = stamp.Center + Vector2.one * padding;
            var bounds = stampBounds;
            stampBounds = index == 0 ? new Vector4(lo.x, lo.y, hi.x, hi.y) :
                new Vector4(Mathf.Min(bounds.x, lo.x), Mathf.Min(bounds.y, lo.y),
                    Mathf.Max(bounds.z, hi.x), Mathf.Max(bounds.w, hi.y));
            stampCenters[index] = new Vector4(stamp.Center.x, stamp.Center.y, stamp.HalfWidth, stamp.HalfLength);
            stampAxes[index] = new Vector4(stamp.Axis.x, stamp.Axis.y, strength, travelling ? -1 : 0);
        }

        public void Record(CommandBuffer commands)
        {
            if (!pendingRecord || !displacementMap) return;
            pendingRecord = false;
            hasDisplacement = stampCount > 0;

            const string label = "GotGrass/DisplacementRT";
            commands.BeginSample(label);
            commands.SetRenderTarget(displacementMap);
            commands.SetViewport(new Rect(0, 0, MapSize, MapSize));
            commands.ClearRenderTarget(false, true, Color.clear);
            if (hasDisplacement)
            {
                commands.SetBufferData(stampCenterBuffer, stampCenters, 0, 0, stampCount);
                commands.SetBufferData(stampAxisBuffer, stampAxes, 0, 0, stampCount);
                var brush = settings.BrushTexture;
                stampProperties.SetVector("_GotGrassTrailBrushParams", new Vector4(
                    Mathf.Clamp01(settings.ForwardWeight), Mathf.Clamp01(settings.SideWeight), brush ? 1 : 0, 0));
                stampProperties.SetTexture("_GotGrassTrailBrush", brush ? brush : Texture2D.grayTexture);
                stampProperties.SetVector(WindowId, new Vector4(mapCenter.x, mapCenter.y, 1 / MapWidth, 0));
                commands.DrawProcedural(Matrix4x4.identity, stampMaterial, 0, MeshTopology.Triangles, 6, stampCount, stampProperties);
            }
            commands.EndSample(label);
        }

        public void Bind(MaterialPropertyBlock properties)
        {
            if (!hasDisplacement || !displacementMap)
            {
                BindDisabled(properties);
                return;
            }

            properties.SetTexture(MapId, displacementMap);
            properties.SetVector(WindowId, new Vector4(mapCenter.x, mapCenter.y, 1 / MapWidth, 0));
            properties.SetVector(BoundsId, stampBounds);
        }

        public static void BindDisabled(MaterialPropertyBlock properties)
        {
            properties.SetTexture(MapId, Texture2D.blackTexture);
            properties.SetVector(WindowId, Vector4.zero);
            properties.SetVector(BoundsId, Vector4.zero);
        }

        public void Dispose()
        {
            stampCenterBuffer?.Release();
            stampAxisBuffer?.Release();
            stampCenterBuffer = stampAxisBuffer = null;
            if (displacementMap) displacementMap.Release();
            MeshResidentHeight.Destroy(displacementMap);
            MeshResidentHeight.Destroy(stampMaterial);
            displacementMap = null;
            stampMaterial = null;
            hasDisplacement = pendingRecord = false;
        }
    }
}
