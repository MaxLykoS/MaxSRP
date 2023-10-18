using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    
    public class MaxShadowCasterPass
    {
        private const int SHADOWMAP_RESOLUTION = 2048;
        private const int CASCADE_COUNT = 4;
        private RenderTexture m_mainShadowMapRT;

        private Matrix4x4[] m_worldToCascadeShadowMapMatrices = new Matrix4x4[CASCADE_COUNT];
        private Vector4[] m_cascadeCullingSpheres = new Vector4[CASCADE_COUNT];

        public MaxShadowCasterPass()
        {
            Shader.SetGlobalInt(ShaderProperties._ShadowMapWidth, SHADOWMAP_RESOLUTION);  // constant
            m_mainShadowMapRT = new RenderTexture(SHADOWMAP_RESOLUTION, SHADOWMAP_RESOLUTION, 24, RenderTextureFormat.Shadowmap, RenderTextureReadWrite.Linear);
            m_mainShadowMapRT.name = "MainShadowMap";
        }

        // 通过ComputeDirectionalShadowMatricesAndCullingPrimitives得到的投影矩阵，其对应的x,y,z范围分别为均为(-1,1).
        // 因此我们需要构造坐标变换矩阵，可以将世界坐标转换到ShadowMap齐次坐标空间。对应的xy范围为(0,1),z范围为(1,0)
        static Matrix4x4 GetWorldToCascadeShadowMapSpaceMatrix(Matrix4x4 proj, Matrix4x4 view, Vector4 cascadeOffsetAndScale)
        {
            //检查平台是否zBuffer反转,一般情况下，z轴方向是朝屏幕内，即近小远大。但是在zBuffer反转的情况下，z轴是朝屏幕外，即近大远小。
            if (SystemInfo.usesReversedZBuffer)
            {
                proj.m20 = -proj.m20;
                proj.m21 = -proj.m21;
                proj.m22 = -proj.m22;
                proj.m23 = -proj.m23;
            }

            Matrix4x4 worldToShadow = proj * view;

            // xyz = xyz * 0.5 + 0.5;
            // 即将xy从(-1,1)映射到(0,1)，z从(-1,1)或(1,-1)映射到(0,1)或(1,0)

            var textureScaleAndBias = Matrix4x4.identity;
            // x = x * 0.5 + 0.5
            textureScaleAndBias.m00 = 0.5f;
            textureScaleAndBias.m03 = 0.5f;

            // y = y * 0.5 + 0.5
            textureScaleAndBias.m11 = 0.5f;
            textureScaleAndBias.m13 = 0.5f;

            // z = z * 0.5 = 0.5
            textureScaleAndBias.m22 = 0.5f;
            textureScaleAndBias.m23 = 0.5f;

            //再将uv映射到cascadeShadowMap的空间
            var cascadeOffsetAndScaleMatrix = Matrix4x4.identity;

            //x = x * cascadeOffsetAndScale.z + cascadeOffsetAndScale.x
            cascadeOffsetAndScaleMatrix.m00 = cascadeOffsetAndScale.z;
            cascadeOffsetAndScaleMatrix.m03 = cascadeOffsetAndScale.x;

            //y = y * cascadeOffsetAndScale.w + cascadeOffsetAndScale.y
            cascadeOffsetAndScaleMatrix.m11 = cascadeOffsetAndScale.w;
            cascadeOffsetAndScaleMatrix.m13 = cascadeOffsetAndScale.y;

            return cascadeOffsetAndScaleMatrix * textureScaleAndBias * worldToShadow;
        }

        private void ClearAndActiveShadowMapTexture(ScriptableRenderContext context, int shadowMapResolution)
        {
            CommandBuffer cmd = CommandBufferPool.Get("ClearShadowMap");

            //设置渲染目标
            cmd.SetRenderTarget(m_mainShadowMapRT);
            //Clear贴图
            cmd.ClearRenderTarget(true, true, Color.black, 1);

            context.ExecuteCommandBuffer(cmd);
        }
        private void SetupShadowCascade(ScriptableRenderContext context, Vector2 offsetInAtlas, int resolution, ref Matrix4x4 matrixView, ref Matrix4x4 matrixProj)
        {
            CommandBuffer cmd = CommandBufferPool.Get("SetupShadowCascade");

            cmd.SetViewport(new Rect(offsetInAtlas.x, offsetInAtlas.y, resolution, resolution));
            //设置view&proj矩阵
            cmd.SetViewProjectionMatrices(matrixView, matrixProj);
            context.ExecuteCommandBuffer(cmd);
        }

        public void Execute(ScriptableRenderContext context, ShadowCasterSetting setting)
        {
            CommandBuffer cmd = CommandBufferPool.Get("SetupShadowCasterPass");

            int mainLightIndex = setting.m_MainLightIndex;
            ref VisibleLight mainLight = ref setting.m_VisibleLight;
            ref CullingResults cullingResults = ref setting.m_CullingResults;

            if (!setting.HasMainLight)
            {
                //表示场景无主灯光
                cmd.SetGlobalVector(ShaderProperties._ShadowParams, new Vector4(0, 0, 0, 0));
                return;
            }

            //false表示该灯光对场景无影响
            if (!cullingResults.GetShadowCasterBounds(mainLightIndex, out Bounds lightBounds))
            {
                cmd.SetGlobalVector(ShaderProperties._ShadowParams, new Vector4(0, 0, 0, 0));
                return;
            }
            Light lightComponent = mainLight.light;

            Vector3 cascadeRatio = setting.m_ShadowSetting.CascadeRatio;

            cmd.SetGlobalTexture(ShaderProperties._MainShadowMap, m_mainShadowMapRT);

            this.ClearAndActiveShadowMapTexture(context, SHADOWMAP_RESOLUTION);

            int cascadeAtlasGridSize = Mathf.CeilToInt(Mathf.Sqrt(CASCADE_COUNT));
            int cascadeResolution = SHADOWMAP_RESOLUTION / cascadeAtlasGridSize;

            Vector2 cascadeOffsetInAtlas = new Vector2(0, 0);

            for (int i = 0; i < CASCADE_COUNT; i++)
            {
                int x = i % cascadeAtlasGridSize;
                int y = i / cascadeAtlasGridSize;

                //计算当前级别的级联阴影在Atlas上的偏移位置
                Vector2 offsetInAtlas = new Vector2(x * cascadeResolution, y * cascadeResolution);

                //get light matrixView,matrixProj,shadowSplitData
                cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(mainLightIndex, i, CASCADE_COUNT,
                cascadeRatio, cascadeResolution, lightComponent.shadowNearPlane, out var matrixView, out var matrixProj, out var shadowSplitData);

                //generate ShadowDrawingSettings
                ShadowDrawingSettings shadowDrawSetting = new ShadowDrawingSettings(cullingResults, mainLightIndex);
                shadowDrawSetting.splitData = shadowSplitData;

                //设置Cascade相关参数
                SetupShadowCascade(context, offsetInAtlas, cascadeResolution, ref matrixView, ref matrixProj);

                //绘制阴影
                context.DrawShadows(ref shadowDrawSetting);


                //计算Cascade ShadowMap空间投影矩阵和包围圆
                Vector4 cascadeOffsetAndScale = new Vector4(offsetInAtlas.x, offsetInAtlas.y, cascadeResolution, cascadeResolution) / SHADOWMAP_RESOLUTION;
                Matrix4x4 matrixWorldToShadowMapSpace = GetWorldToCascadeShadowMapSpaceMatrix(matrixProj, matrixView, cascadeOffsetAndScale);
                m_worldToCascadeShadowMapMatrices[i] = matrixWorldToShadowMapSpace;
                m_cascadeCullingSpheres[i] = shadowSplitData.cullingSphere;
            }

            //setup shader params
            cmd.SetGlobalMatrixArray(ShaderProperties._WorldToMainLightCascadeShadowMapSpaceMatrices, m_worldToCascadeShadowMapMatrices);
            cmd.SetGlobalVectorArray(ShaderProperties._CascadeCullingSpheres, m_cascadeCullingSpheres);
            //将阴影的一些数据传入Shader
            cmd.SetGlobalVector(ShaderProperties._ShadowParams, new Vector4(lightComponent.shadowBias, lightComponent.shadowNormalBias, lightComponent.shadowStrength, CASCADE_COUNT));

            context.ExecuteCommandBuffer(cmd);
        }

        public struct ShadowCasterSetting
        {
            public ShadowSetting m_ShadowSetting;
            public CullingResults m_CullingResults;
            public int m_MainLightIndex;
            public VisibleLight m_VisibleLight;

            public bool HasMainLight
            {
                get { return m_MainLightIndex != -1; }
            }
        }
        public static class ShaderProperties
        {
            /// 类型Matrix4x4[4]，表示每级Cascade从世界到贴图空间的转换矩阵
            public static readonly int _WorldToMainLightCascadeShadowMapSpaceMatrices = Shader.PropertyToID("_WorldToMainLightCascadeShadowMapSpaceMatrices");

            /// 类型Vector4[4],表示每级Cascade的空间裁剪包围球
            public static readonly int _CascadeCullingSpheres = Shader.PropertyToID("_CascadeCullingSpheres");

            //x为depthBias,y为normalBias,z为shadowStrength
            public static readonly int _ShadowParams = Shader.PropertyToID("_ShadowParams");
            public static readonly int _MainShadowMap = Shader.PropertyToID("_MainShadowMap");

            public static readonly int _ShadowMapWidth = Shader.PropertyToID("_ShadowMapWidth");
        }
    }
}
