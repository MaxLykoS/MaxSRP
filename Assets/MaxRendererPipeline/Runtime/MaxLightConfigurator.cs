using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Shared with ClusteredLightInput.hlsl; two float4s, 32 bytes per light.
    public struct MaxPointLightData
    {
        public Vector4 positionRange;
        public Vector4 color;
    }

    public class MaxLightConfigurator
    {
        private const int MAX_VISIBLE_POINT_LIGHTS = 32;
        private readonly Vector4[] m_pointLightPositionAndRanges = new Vector4[MAX_VISIBLE_POINT_LIGHTS];
        private readonly Vector4[] m_pointLightColors = new Vector4[MAX_VISIBLE_POINT_LIGHTS];
        private MaxPointLightData[] m_pointLights = new MaxPointLightData[64];
        public MaxPointLightData[] PointLights => m_pointLights;
        public int PointLightCount { get; private set; }

        public void SetupMultiShaderLightingParams(ScriptableRenderContext context, ref CullingResults cullingResults, SunLightData sun)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Setup Global Light Data");
            try
            {
                NativeArray<int> lightIndexingMap = cullingResults.GetLightIndexMap(Allocator.Temp);
                try
                {
                    int pointLightCount = 0;
                    NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
                    for (int i = 0; i < lightIndexingMap.Length; ++i)
                        lightIndexingMap[i] = -1;
                    for (int i = 0; i < visibleLights.Length; ++i)
                    {
                        VisibleLight light = visibleLights[i];
                        switch (light.lightType)
                        {
                            case LightType.Point:
                                if (pointLightCount == m_pointLights.Length)
                                    System.Array.Resize(ref m_pointLights, m_pointLights.Length * 2);
                                Vector4 positionAndRange = light.light.transform.position;
                                positionAndRange.w = light.range;
                                // Upload candela / HDRScale; shader inverse-square falloff
                                // turns this into illuminance / HDRScale at the surface.
                                Vector4 color = LightPhotometry.PointColor(light.light);
                                m_pointLights[pointLightCount] = new MaxPointLightData { positionRange = positionAndRange, color = color };
                                // Preserve the legacy uniform interface for offline shader fixtures.
                                // Runtime lighting uses the complete structured buffer below.
                                if (pointLightCount < MAX_VISIBLE_POINT_LIGHTS)
                                {
                                    m_pointLightPositionAndRanges[pointLightCount] = positionAndRange;
                                    m_pointLightColors[pointLightCount] = color;
                                }
                                ++pointLightCount;
                                break;
                        }
                    }
                    cullingResults.SetLightIndexMap(lightIndexingMap);
                    PointLightCount = pointLightCount;
                    // Directional lights in visibleLights never select/replace the sun.
                    // Write zeros as well, so removing the sun cannot retain an old direction.
                    cmd.SetGlobalVector(ShaderProperties._DirectionalLightDirection, sun.Direction);
                    cmd.SetGlobalColor(ShaderProperties._DirectionalLightColor, sun.LinearColor);
                    cmd.SetGlobalVectorArray(ShaderProperties._PointLightPositionAndRanges, m_pointLightPositionAndRanges);
                    cmd.SetGlobalVectorArray(ShaderProperties._PointLightColors, m_pointLightColors);
                    cmd.SetGlobalInteger(ShaderProperties._PointLightCount, Mathf.Min(pointLightCount, MAX_VISIBLE_POINT_LIGHTS));
                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    lightIndexingMap.Dispose();
                }
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        public class ShaderProperties
        {
            public static readonly int _DirectionalLightDirection = Shader.PropertyToID("_DirectionalLightDirection");
            public static readonly int _DirectionalLightColor = Shader.PropertyToID("_DirectionalLightColor");
            public static readonly int _PointLightPositionAndRanges = Shader.PropertyToID("_PointLightPositionAndRanges");
            public static readonly int _PointLightColors = Shader.PropertyToID("_PointLightColors");
            public static readonly int _PointLightCount = Shader.PropertyToID("_PointLightCount");
        }
    }
}
