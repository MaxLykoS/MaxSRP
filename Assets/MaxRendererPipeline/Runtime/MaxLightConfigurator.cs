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
        private const int MAX_VISIBLE_POINT_LIGHTS = 32;
        private Vector4[] m_pointLightPositionAndRanges = new Vector4[MAX_VISIBLE_POINT_LIGHTS];
        private Vector4[] m_pointLightColors = new Vector4[MAX_VISIBLE_POINT_LIGHTS];

        // 拿到主光index，拿到另外4个点光的数据
        // 因为是延迟管线 + cluster lighting(未实现)，不需要per draw light cbuffer，所以拿到光源数据后直接把所有光源都给-1
        // 目前只给点光留了4个
        public (int mainLightIndex, VisibleLight mainLight) SetupMultiShaderLightingParams(ScriptableRenderContext context, ref CullingResults cullingResults)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Setup Global Light Data");

            int pointLightCount = 0;
            int mainLightIndex = -1;

            NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
            NativeArray<int> lightIndexingMap = cullingResults.GetLightIndexMap(Allocator.Temp);
            for (int i = 0; i < lightIndexingMap.Length; ++i)
                lightIndexingMap[i] = -1;
            for (int i = 0; i < visibleLights.Length; ++i)
            {
                VisibleLight light = visibleLights[i];

                switch (light.lightType)
                {
                    case LightType.Directional:
                        mainLightIndex = i;
                        break;
                    case LightType.Point:
                        // 收集点光信息
                        Vector4 positionAndRange = light.light.gameObject.transform.position;
                        positionAndRange.w = light.range;
                        m_pointLightPositionAndRanges[pointLightCount] = positionAndRange;
                        m_pointLightColors[pointLightCount] = light.finalColor;
                        ++pointLightCount;
                        break;
                    default:
                        break;
                }

                if (pointLightCount >= MAX_VISIBLE_POINT_LIGHTS)
                    break;
            }
            cullingResults.SetLightIndexMap(lightIndexingMap); // 全给-1，不需要per draw data

            #region 设置主光
            bool hasMainLight = mainLightIndex != -1;
            if (hasMainLight)  // has main light
            {
                var mainLight = visibleLights[mainLightIndex];
                var forward = -(Vector4)mainLight.light.gameObject.transform.forward;
                cmd.SetGlobalVector(ShaderProperties._DirectionalLightDirection, forward);
                cmd.SetGlobalVector(ShaderProperties._DirectionalLightDirection, forward);
                cmd.SetGlobalColor(ShaderProperties._DirectionalLightColor, mainLight.finalColor);
            }
            else
            {
                cmd.SetGlobalColor(ShaderProperties._DirectionalLightColor, new Color(0, 0, 0, 0));
            }
            #endregion

            #region 设置点光
            cmd.SetGlobalVectorArray(ShaderProperties._PointLightPositionAndRanges, m_pointLightPositionAndRanges);
            cmd.SetGlobalVectorArray(ShaderProperties._PointLightColors, m_pointLightColors);
            cmd.SetGlobalInteger(ShaderProperties._PointLightCount, pointLightCount);
            #endregion

            context.ExecuteCommandBuffer(cmd);

            return (mainLightIndex, hasMainLight ? visibleLights[mainLightIndex] : default);
        }

        public class ShaderProperties
        {
            // 方向光
            public static readonly int _DirectionalLightDirection = Shader.PropertyToID("_DirectionalLightDirection");
            public static readonly int _DirectionalLightColor = Shader.PropertyToID("_DirectionalLightColor");

            // 点光源
            public static readonly int _PointLightPositionAndRanges = Shader.PropertyToID("_PointLightPositionAndRanges");
            public static readonly int _PointLightColors = Shader.PropertyToID("_PointLightColors");
            public static readonly int _PointLightCount = Shader.PropertyToID("_PointLightCount");
        }
    }
}
