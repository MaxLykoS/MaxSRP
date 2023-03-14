using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using System;


namespace MaxSRP
{
    public class MaxLightConfigurator
    {
        public class ShaderProperties
        {
            public static int AmbientColor = Shader.PropertyToID("_AmbientColor");

            // 方向光
            public static int DirectionalLightDirection = Shader.PropertyToID("_MaxDirectionalLightDirection");
            public static int DirectionalLightColor = Shader.PropertyToID("_MaxDirectionalLightColor");

            // 点光源
            public static int OtherLightPositionAndRanges = Shader.PropertyToID("_MaxOtherLightPositionAndRanges");
            public static int OtherLightColors = Shader.PropertyToID("_MaxOtherLightColors");
        }

        private int m_mainLightIndex = -1;
        private const int MAX_VISIBLE_OTHER_LIGHTS = 32;
        private Vector4[] m_otherLightPositionAndRanges = new Vector4[MAX_VISIBLE_OTHER_LIGHTS];
        private Vector4[] m_otherLightColors = new Vector4[MAX_VISIBLE_OTHER_LIGHTS];

        private static int GetMainLightIndex(NativeArray<VisibleLight> lights)
        {
            for (int i = 0; i < lights.Length; ++i)
            {
                if (lights[i].lightType == LightType.Directional)
                {
                    return i;
                }
            }
            return -1;
        }

        public LightData SetupMultiShaderLightingParams(ref CullingResults cullingResults)
        {
            NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
            m_mainLightIndex = GetMainLightIndex(visibleLights);
            if (m_mainLightIndex >= 0)
            {
                var mainLight = visibleLights[m_mainLightIndex];
                var forward = -(Vector4)mainLight.light.gameObject.transform.forward;
                Shader.SetGlobalVector(ShaderProperties.DirectionalLightDirection, forward);
                Shader.SetGlobalColor(ShaderProperties.DirectionalLightColor, mainLight.finalColor);
            }
            else
            {
                Shader.SetGlobalColor(ShaderProperties.DirectionalLightColor, new Color(0, 0, 0, 0));
            }

            SetupOtherLightDatas(ref cullingResults);

            Shader.SetGlobalColor(ShaderProperties.AmbientColor, RenderSettings.ambientLight);

            LightData ld = new LightData()
            {
                mainLight = m_mainLightIndex >= 0 && m_mainLightIndex < visibleLights.Length? visibleLights[m_mainLightIndex] : default,
                mainLightIndex = m_mainLightIndex
            };
            return ld;
        }

        //设置非平行光源的GPU数据
        private void SetupOtherLightDatas(ref CullingResults cullingResults)
        {
            NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
            NativeArray<int> lightMapIndex = cullingResults.GetLightIndexMap(Allocator.Temp);
            int otherLightIndex = 0;
            int visibleLightIndex = 0;
            foreach (VisibleLight light in visibleLights)
            {
                switch (light.lightType)
                {
                    case LightType.Directional:
                        lightMapIndex[visibleLightIndex] = -1;
                        break;
                    case LightType.Point:
                        lightMapIndex[visibleLightIndex] = otherLightIndex;

                        // setup point light
                        Vector4 positionAndRange = light.light.gameObject.transform.position;
                        positionAndRange.w = light.range;
                        m_otherLightPositionAndRanges[otherLightIndex] = positionAndRange;
                        m_otherLightColors[otherLightIndex] = light.finalColor;

                        otherLightIndex++;
                        break;
                    default:
                        lightMapIndex[visibleLightIndex] = -1;
                        break;
                }
                visibleLightIndex++;
            }
            for (var i = visibleLightIndex; i < lightMapIndex.Length; i++)
            {
                lightMapIndex[i] = -1;
            }
            cullingResults.SetLightIndexMap(lightMapIndex);
            Shader.SetGlobalVectorArray(ShaderProperties.OtherLightPositionAndRanges, m_otherLightPositionAndRanges);
            Shader.SetGlobalVectorArray(ShaderProperties.OtherLightColors, m_otherLightColors);
        }
    }

    public struct LightData
    {
        public int mainLightIndex;
        public VisibleLight mainLight;

        public bool HasMainLight()
        {
            return mainLightIndex >= 0;
        }
    }
}
