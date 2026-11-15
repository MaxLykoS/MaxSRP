using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class CloudLayerSettings
    {
        public bool Enabled;
        [Tooltip("Square-root encoded planar density map. Both axes repeat in world XZ.")]
        public Texture2D DensityMap;
        [Tooltip("Serialized to retain the cache shader in Player builds.")]
        public Shader LightingShader;
        [Range(0, 1)] public float Coverage = 0.52f;
        [Range(0, 10)] public float Density = 3;
        [Min(100), Tooltip("Cloud plane altitude above sea level, in metres.")] public float Altitude = 2000;
        [Min(100)] public float Thickness = 700;
        [Min(1000), Tooltip("World-space density tile size in metres. Wind translates this field continuously.")] public float TileSize = 16000;
        [Range(0, 360)] public float WindDirection = 30;
        [Min(0)] public float WindSpeed = 12;
        [Range(0, 2)] public float AmbientStrength = 0.8f;
        [Range(64, 1024)] public int LightingResolution = 256;
        [Range(4, 32)] public int LightingSteps = 16;
        [Range(0.1f, 5)] public float SunUpdateAngle = 0.5f;
        [Min(0.1f)] public float EnvironmentUpdateInterval = 1;
    }

    // Periodic cloud-map lighting, shared by cameras. No cloud color history.
    public sealed class MaxCloudLayerPass : IDisposable
    {
        private readonly CloudLayerSettings m_settings;
        private readonly ComputeShader m_ibl;
        private readonly int m_kernel;
        private Material m_bake;
        private RenderTexture m_lighting;
        private Vector4 m_shape, m_geometry, m_wind;
        private Vector3 m_cachedSun;
        private Texture m_map;
        private int m_steps, m_revision, m_atmosphereRevision = -1;
        private float m_ambient, m_seaLevel;
        private bool m_valid, m_enabled;
        private double m_environmentTime = -1;
        private Vector2 m_environmentAnchor;
        private bool m_anchorValid;
        private double m_windX, m_windZ, m_lastWindTime = -1;
        private Vector2 m_worldWindVelocity;
        public double FrameTime { get; private set; }
        public int ContentRevision { get; private set; }
        public bool IsEnabled => m_enabled;
        public RenderTexture LightingCache => m_lighting;
        public Vector2 WindVelocity { get; private set; }

        public MaxCloudLayerPass(CloudLayerSettings settings, ComputeShader ibl)
        {
            m_settings = settings; m_ibl = ibl;
            m_kernel = ibl.FindKernel("SkyToEnvironment");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged += Invalidate;
#endif
        }
        private void Invalidate() { m_valid = false; ++ContentRevision; }
        public int EnvironmentRevision(int atmosphereRevision)
        {
            if (m_atmosphereRevision != atmosphereRevision) { m_atmosphereRevision = atmosphereRevision; ++m_revision; }
            return m_revision;
        }
        public Vector2 WindDelta(double previousTime) => WindVelocity * (float)Math.Max(0, FrameTime - previousTime);
        public Vector3 ObserverDelta(Vector3 previous, Vector3 current) => m_enabled ? (previous - current) / m_shape.w : Vector3.zero;
        private static float Wrap(double value) => (float)(value - Math.Floor(value));

        public void Prepare(ScriptableRenderContext context, Camera camera, SunLightData sun, SunLightData skySun,
            MaxProceduralSkyboxPass.AtmosphereSettings atmosphere, Texture transmittance, float environmentHeight)
        {
            FrameTime = Time.realtimeSinceStartupAsDouble;
            if (m_lastWindTime >= 0)
            {
                double dt = Math.Max(0, FrameTime - m_lastWindTime);
                m_windX += m_worldWindVelocity.x * dt;
                m_windZ += m_worldWindVelocity.y * dt;
            }
            m_lastWindTime = FrameTime;
            bool enabled = m_settings.Enabled && m_settings.DensityMap != null && m_settings.Density > 0;
            bool changed = enabled != m_enabled;
            var cmd = CommandBufferPool.Get("Cloud Layer Lighting");
            try
            {
                if (enabled)
                {
                    if (m_settings.LightingShader == null || !m_settings.LightingShader.isSupported)
                        throw new InvalidOperationException("Cloud Layer requires its serialized lighting shader.");
                    if (m_bake == null || m_bake.shader != m_settings.LightingShader)
                    {
                        MaxRenderResources.Destroy(ref m_bake);
                        m_bake = new Material(m_settings.LightingShader) { hideFlags = HideFlags.HideAndDontSave };
                        m_valid = false;
                    }
                    int resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(m_settings.LightingResolution, 64, 1024));
                    if (m_lighting == null || m_lighting.width != resolution || !m_lighting.IsCreated())
                    {
                        MaxRenderResources.Destroy(ref m_lighting);
                        var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf)
                            ? RenderTextureFormat.RGHalf : RenderTextureFormat.ARGBHalf;
                        m_lighting = new RenderTexture(resolution, resolution, 0, format, RenderTextureReadWrite.Linear)
                        {
                            name = "Cloud Sun Visibility", filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave
                        };
                        if (!m_lighting.Create()) throw new InvalidOperationException("Cannot allocate cloud lighting cache.");
                        m_valid = false;
                    }
                    var shape = new Vector4(1, Mathf.Clamp01(m_settings.Coverage), Mathf.Clamp(m_settings.Density, 0, 10), Mathf.Max(1000, m_settings.TileSize));
                    var geometry = new Vector4(Mathf.Max(100, m_settings.Altitude), Mathf.Max(100, m_settings.Thickness), Mathf.Max(1000, atmosphere._PlanetRadius * MaxProceduralSkyboxPass.AtmosphereSettings.KilometresToMetres), 0);
                    int steps = Mathf.Clamp(m_settings.LightingSteps, 4, 32);
                    float angle = m_settings.WindDirection * Mathf.Deg2Rad;
                    Vector2 velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (Mathf.Max(0, m_settings.WindSpeed) / shape.w);
                    changed |= m_map != m_settings.DensityMap || !shape.Equals(m_shape) || !geometry.Equals(m_geometry) ||
                        m_steps != steps || !m_ambient.Equals(m_settings.AmbientStrength) || WindVelocity != velocity ||
                        !m_seaLevel.Equals(atmosphere._SeaLevel);
                    bool dirty = !m_valid || changed || Vector3.Angle(m_cachedSun, sun.AtmosphereDirection) >= Mathf.Clamp(m_settings.SunUpdateAngle, 0.1f, 5);
                    m_shape = shape; m_geometry = geometry; m_map = m_settings.DensityMap;
                    m_steps = steps; m_ambient = m_settings.AmbientStrength; WindVelocity = velocity;
                    m_seaLevel = atmosphere._SeaLevel;
                    m_worldWindVelocity = velocity * shape.w;
                    // Features move with +velocity; texture sampling moves oppositely.
                    m_wind = new Vector4(Wrap(-m_windX / shape.w), Wrap(-m_windZ / shape.w), velocity.x, velocity.y);
                    if (dirty)
                    {
                        m_bake.SetTexture("_MaxCloudMap", m_map);
                        m_bake.SetVector("_MaxCloudShape", shape);
                        m_bake.SetVector("_MaxCloudGeometry", geometry);
                        m_bake.SetVector("_MaxCloudBakeSun", sun.AtmosphereDirection);
                        m_bake.SetInt("_MaxCloudSteps", steps);
                        cmd.BeginSample("Cloud Light Bake");
#if UNITY_EDITOR
                        UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                        cmd.SetRenderTarget(m_lighting);
                        cmd.SetViewport(new Rect(0, 0, resolution, resolution));
                        cmd.DrawProcedural(Matrix4x4.identity, m_bake, 0, MeshTopology.Triangles, 3);
#if UNITY_EDITOR
                        UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                        cmd.EndSample("Cloud Light Bake");
                        m_cachedSun = sun.AtmosphereDirection;
                        ++m_revision;
                    }
                    if (changed || m_environmentTime < 0 ||
                        (velocity.sqrMagnitude > 0 && FrameTime - m_environmentTime >= Mathf.Max(0.1f, m_settings.EnvironmentUpdateInterval)))
                    { m_environmentTime = FrameTime; ++m_revision; }
                }
                else
                {
                    m_shape = Vector4.zero; WindVelocity = Vector2.zero; m_worldWindVelocity = Vector2.zero;
                    if (m_enabled) { ++m_revision; MaxRenderResources.Destroy(ref m_lighting); m_valid = false; }
                }
                m_enabled = enabled;
                if (changed) ++ContentRevision;
                Vector3 position = camera.transform.position;
                var observer = new Vector4(position.x, position.y - atmosphere._SeaLevel * MaxProceduralSkyboxPass.AtmosphereSettings.KilometresToMetres, position.z, 0);
                var anchor = new Vector2(Mathf.Round(position.x / 128) * 128, Mathf.Round(position.z / 128) * 128);
                if (enabled && (!m_anchorValid || anchor != m_environmentAnchor))
                { m_environmentAnchor = anchor; m_anchorValid = true; ++m_revision; }
                Texture map = enabled ? m_map : Texture2D.blackTexture;
                Texture light = enabled ? (Texture)m_lighting : Texture2D.whiteTexture;
                var ambient = new Vector4(Mathf.Clamp(m_settings.AmbientStrength, 0, 2), 0, 0, 0);
                cmd.SetGlobalTexture("_MaxCloudMap", map);
                cmd.SetGlobalTexture("_MaxCloudLighting", light);
                cmd.SetGlobalVector("_MaxCloudShape", m_shape);
                cmd.SetGlobalVector("_MaxCloudGeometry", m_geometry);
                cmd.SetGlobalVector("_MaxCloudWind", m_wind);
                cmd.SetGlobalVector("_MaxCloudAmbient", ambient);
                cmd.SetGlobalVector("_MaxCloudObserver", observer);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxCloudMap", map);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_MaxCloudLighting", light);
                cmd.SetComputeVectorParam(m_ibl, "_MaxCloudShape", m_shape);
                cmd.SetComputeVectorParam(m_ibl, "_MaxCloudGeometry", m_geometry);
                cmd.SetComputeVectorParam(m_ibl, "_MaxCloudWind", m_wind);
                cmd.SetComputeVectorParam(m_ibl, "_MaxCloudAmbient", ambient);
                cmd.SetComputeVectorParam(m_ibl, "_MaxCloudObserver", new Vector4(m_environmentAnchor.x,
                    Mathf.Max(1, environmentHeight), m_environmentAnchor.y, 0));
                // Compute resources are bound explicitly; sky material parameters
                // are not inherited by the environment kernel.
                cmd.SetComputeFloatParam(m_ibl, "_PlanetRadius", atmosphere._PlanetRadius);
                cmd.SetComputeFloatParam(m_ibl, "_AtmosphereHeight", atmosphere._AtmosphereHeight);
                cmd.SetComputeVectorParam(m_ibl, "_SolarIrradiance", skySun.LinearColor);
                cmd.SetComputeVectorParam(m_ibl, "_AtmosphereSunDirection", skySun.AtmosphereDirection);
                cmd.SetComputeTextureParam(m_ibl, m_kernel, "_TransmittanceLUT", transmittance);
                context.ExecuteCommandBuffer(cmd);
                m_valid = enabled;
            }
            catch { m_valid = false; throw; }
            finally { CommandBufferPool.Release(cmd); }
        }
        public void Dispose()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged -= Invalidate;
#endif
            Shader.SetGlobalVector("_MaxCloudShape", Vector4.zero);
            MaxRenderResources.UnbindTexture("_MaxCloudLighting", m_lighting);
            MaxRenderResources.Destroy(ref m_lighting);
            MaxRenderResources.Destroy(ref m_bake);
        }
    }
}
