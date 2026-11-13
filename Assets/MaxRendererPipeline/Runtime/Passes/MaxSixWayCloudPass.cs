using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class SixWayCloudSettings
    {
        public bool Enabled;
        public Texture2D PositiveAxes;
        public Texture2D NegativeAxes;
        public Shader Shader;
        [Min(100)] public float Altitude = 2200;
        [Min(1000)] public float FieldSize = 30000;
        [Min(100)] public float CloudWidth = 4200;
        [Range(0, 360)] public float WindDirection = 30;
        [Min(0)] public float WindSpeed = 12;
        [Range(0, 4)] public float DirectStrength = 1;
        [Range(0, 4)] public float AmbientStrength = 0.8f;
        [Range(0, 1)] public float Opacity = 1;
        [Min(0.1f)] public float EnvironmentUpdateInterval = 1;
    }

    // Twenty-five world-anchored sample cards. The atlas contains eight static
    // shapes, not a flipbook. Only their orientation faces the observer; their
    // centres translate in world metres and do not orbit the camera.
    public sealed class MaxSixWayCloudPass : IDisposable
    {
        public const int CardCount = 25;
        private readonly SixWayCloudSettings m_settings;
        private readonly ComputeShader m_ibl;
        private readonly int m_kernel;
        private Material m_material;
        private readonly Vector4[] m_centres = new Vector4[32];
        private readonly Vector4[] m_right = new Vector4[32];
        private readonly Vector4[] m_up = new Vector4[32];
        private double m_time = -1, m_windX, m_windZ, m_environmentTime = -1;
        private Vector2 m_velocity;
        private int m_settingsHash, m_environmentRevision, m_lastAtmosphereRevision = -1;
        private Vector2Int m_environmentAnchor;
        private bool m_enabled;
        public bool IsEnabled => m_enabled;
        public int ContentRevision { get; private set; }

        public MaxSixWayCloudPass(SixWayCloudSettings settings, ComputeShader ibl)
        {
            m_settings = settings;
            m_ibl = ibl;
            m_kernel = ibl.FindKernel("SkyToEnvironment");
        }

        private static float Hash(int index, int salt)
        {
            uint value = unchecked((uint)(index * 73856093 ^ salt * 19349663));
            value ^= value >> 13; value *= 1274126177; value ^= value >> 16;
            return (value & 0xffffff) / 16777216f;
        }
        private static float Wrap(double position, float observer, float size)
            => (float)(position - Math.Floor((position - observer) / size + 0.5) * size);
        private int SettingsHash(float seaLevel)
        {
            unchecked
            {
                int hash = m_settings.Enabled ? 1 : 0;
                hash = hash * 31 + (m_settings.PositiveAxes == null ? 0 : m_settings.PositiveAxes.GetInstanceID());
                hash = hash * 31 + (m_settings.NegativeAxes == null ? 0 : m_settings.NegativeAxes.GetInstanceID());
                hash = hash * 31 + (m_settings.Shader == null ? 0 : m_settings.Shader.GetInstanceID());
                hash = hash * 31 + m_settings.Altitude.GetHashCode();
                hash = hash * 31 + m_settings.FieldSize.GetHashCode();
                hash = hash * 31 + m_settings.CloudWidth.GetHashCode();
                hash = hash * 31 + m_settings.DirectStrength.GetHashCode();
                hash = hash * 31 + m_settings.AmbientStrength.GetHashCode();
                hash = hash * 31 + m_settings.Opacity.GetHashCode();
                return hash * 31 + seaLevel.GetHashCode();
            }
        }

        public int EnvironmentRevision(int atmosphereRevision)
        {
            if (m_lastAtmosphereRevision != atmosphereRevision)
            { m_lastAtmosphereRevision = atmosphereRevision; ++m_environmentRevision; }
            return m_environmentRevision;
        }

        public void Prepare(ScriptableRenderContext context, Camera camera, SunLightData sun,
            MaxProceduralSkyboxPass.AtmosphereSettings atmosphere, Texture transmittance, Texture sky,
            Vector3 previousCameraPosition, float environmentViewHeight, Vector4 skyViewSizeAndInvSize)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (m_time >= 0)
            {
                double dt = Math.Max(0, now - m_time);
                m_windX += m_velocity.x * dt; m_windZ += m_velocity.y * dt;
            }
            m_time = now;
            bool enabled = m_settings.Enabled && m_settings.PositiveAxes != null && m_settings.NegativeAxes != null;
            int hash = SettingsHash(atmosphere._SeaLevel * MaxProceduralSkyboxPass.AtmosphereSettings.KilometresToMetres);
            bool changed = enabled != m_enabled || hash != m_settingsHash;
            if (changed) { ++ContentRevision; ++m_environmentRevision; }
            m_settingsHash = hash; m_enabled = enabled;
            float angle = m_settings.WindDirection * Mathf.Deg2Rad;
            m_velocity = enabled ? new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Mathf.Max(0, m_settings.WindSpeed) : Vector2.zero;
            Vector3 observer = camera.transform.position;
            var anchor = new Vector2Int(Mathf.RoundToInt(observer.x / 128), Mathf.RoundToInt(observer.z / 128));
            if (enabled && (anchor != m_environmentAnchor || m_environmentTime < 0 ||
                (m_velocity.sqrMagnitude > 0 && now - m_environmentTime >= Mathf.Max(0.1f, m_settings.EnvironmentUpdateInterval))))
            { m_environmentAnchor = anchor; m_environmentTime = now; ++m_environmentRevision; }

            var cmd = CommandBufferPool.Get("Prepare Six Way Clouds");
            try
            {
                // A conservative sky-only reactive cap shares the existing TAA.
                // No independent color history, cloud resolve or screen texture.
                bool moving = m_velocity.sqrMagnitude > 0 || (previousCameraPosition - observer).sqrMagnitude > 1e-8f;
                cmd.SetGlobalVector("_MaxSixWayTemporal", new Vector4(enabled && moving ? 1 : 0, 0, 0, 0));
                cmd.SetComputeIntParam(m_ibl, "_MaxSixWayCount", enabled ? CardCount : 0);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxSixWayPositive", enabled ? m_settings.PositiveAxes : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxSixWayNegative", enabled ? m_settings.NegativeAxes : Texture2D.blackTexture);
                if (!enabled) return;
                if (m_settings.Shader == null || !m_settings.Shader.isSupported)
                    throw new InvalidOperationException("Six-way clouds require a supported serialized shader.");
                if (m_material == null || m_material.shader != m_settings.Shader)
                {
                    MaxRenderResources.Destroy(ref m_material);
                    m_material = new Material(m_settings.Shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                float size = Mathf.Max(1000, m_settings.FieldSize);
                for (int i = 0; i < CardCount; ++i)
                {
                    float x = (i % 5 - 2 + (Hash(i, 1) - 0.5f) * 0.65f) * size / 5;
                    float z = (i / 5 - 2 + (Hash(i, 2) - 0.5f) * 0.65f) * size / 5;
                    var centre = new Vector3(Wrap(x + m_windX, observer.x, size),
                        atmosphere._SeaLevel * MaxProceduralSkyboxPass.AtmosphereSettings.KilometresToMetres + Mathf.Max(100, m_settings.Altitude) * (0.85f + 0.3f * Hash(i, 3)),
                        Wrap(z + m_windZ, observer.z, size));
                    Vector3 normal = (observer - centre).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, normal);
                    if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                    right.Normalize();
                    Vector3 up = Vector3.Cross(normal, right).normalized;
                    float halfWidth = Mathf.Max(100, m_settings.CloudWidth) * (0.7f + 0.6f * Hash(i, 4)) * 0.5f;
                    float distance = Vector2.Distance(new Vector2(centre.x, centre.z), new Vector2(observer.x, observer.z));
                    // Recycling only occurs outside the visible central disc.
                    float fade = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(size * 0.36f, size * 0.46f, distance));
                    m_centres[i] = new Vector4(centre.x, centre.y, centre.z, i % 8);
                    m_right[i] = new Vector4(right.x, right.y, right.z, halfWidth);
                    m_up[i] = new Vector4(up.x, up.y, up.z, fade);
                }
                // Back-to-front, used identically by raster cards and environment rays.
                for (int i = 1; i < CardCount; ++i)
                    for (int j = i; j > 0 && DistanceSquared(j, observer) > DistanceSquared(j - 1, observer); --j)
                    {
                        Swap(m_centres, j); Swap(m_right, j); Swap(m_up, j);
                    }
                var parameters = new Vector4(1, Mathf.Max(0, m_settings.DirectStrength), Mathf.Max(0, m_settings.AmbientStrength), Mathf.Clamp01(m_settings.Opacity));
                m_material.SetTexture("_MaxSixWayPositive", m_settings.PositiveAxes);
                m_material.SetTexture("_MaxSixWayNegative", m_settings.NegativeAxes);
                m_material.SetTexture("_MaxSixWaySky", sky);
                m_material.SetFloat("_AtmosphereEnvironmentSkyViewHeight", environmentViewHeight);
                m_material.SetVector("_AtmosphereSkyViewSizeAndInvSize", skyViewSizeAndInvSize);
                m_material.SetTexture("_TransmittanceLUT", transmittance);
                m_material.SetVectorArray("_MaxSixWayCentres", m_centres);
                m_material.SetVectorArray("_MaxSixWayRight", m_right);
                m_material.SetVectorArray("_MaxSixWayUp", m_up);
                m_material.SetVector("_MaxSixWayParams", parameters);
                m_material.SetVector("_AtmosphereSunDirection", sun.AtmosphereDirection);
                m_material.SetColor("_SolarIrradiance", sun.LinearColor);
                m_material.SetFloat("_PlanetRadius", atmosphere._PlanetRadius);
                m_material.SetFloat("_AtmosphereHeight", atmosphere._AtmosphereHeight);
                m_material.SetFloat("_SeaLevel", atmosphere._SeaLevel);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxSixWayPositive", m_settings.PositiveAxes);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxSixWayNegative", m_settings.NegativeAxes);
                cmd.SetComputeVectorArrayParam(m_ibl, "_MaxSixWayCentres", m_centres);
                cmd.SetComputeVectorArrayParam(m_ibl, "_MaxSixWayRight", m_right);
                cmd.SetComputeVectorArrayParam(m_ibl, "_MaxSixWayUp", m_up);
                cmd.SetComputeVectorParam(m_ibl, "_MaxSixWayParams", parameters);
                cmd.SetComputeVectorParam(m_ibl, "_MaxSixWayObserver", observer);
                cmd.SetComputeFloatParam(m_ibl, "_MaxSixWaySeaLevel", atmosphere._SeaLevel * MaxProceduralSkyboxPass.AtmosphereSettings.KilometresToMetres);
            }
            finally { context.ExecuteCommandBuffer(cmd); CommandBufferPool.Release(cmd); }
        }
        private float DistanceSquared(int index, Vector3 observer)
            => (new Vector3(m_centres[index].x, m_centres[index].y, m_centres[index].z) - observer).sqrMagnitude;
        private static void Swap(Vector4[] array, int index)
        { Vector4 value = array[index]; array[index] = array[index - 1]; array[index - 1] = value; }

        public void Draw(ScriptableRenderContext context, RenderTexture color, RenderTexture depth, Rect viewport)
        {
            if (!m_enabled || m_material == null) return;
            var cmd = CommandBufferPool.Get("Six Way Main Clouds");
            try
            {
                cmd.SetRenderTarget(color, depth); cmd.SetViewport(viewport);
                cmd.DrawProcedural(Matrix4x4.identity, m_material, 0, MeshTopology.Triangles, 6, CardCount);
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
        }
        public void Dispose() => MaxRenderResources.Destroy(ref m_material);
    }
}
