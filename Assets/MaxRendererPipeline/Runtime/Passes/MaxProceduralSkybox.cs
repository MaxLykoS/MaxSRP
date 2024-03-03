using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxProceduralSkyboxPass
    {
        private Material m_skyboxMat;

        private Material m_transmittanceMat;
        private RenderTexture m_transmittanceLUT;

        private Material m_multiscatteringMat;
        private RenderTexture m_multiscatteringLUT;

        private Material m_skyviewMat;
        private RenderTexture m_skyviewLUT;

        private Material m_aerialPerspectiveLUTMat;
        private RenderTexture m_aerialPerspectiveLUT;

        private AtmosphereSettings m_setting;

        private Material m_aerialPerspectiveMat;
        //private RenderTexture m_screenRTCopy;
        public MaxProceduralSkyboxPass(AtmosphereSettings setting)
        {
            m_setting = setting;

            m_skyboxMat = new Material(setting.ProceduralSkyboxShader);

            m_transmittanceMat = new Material(setting.TransmittanceShader);
            m_transmittanceLUT = new RenderTexture(256, 64, 0, RenderTextureFormat.ARGBFloat);
            m_transmittanceLUT.wrapMode = TextureWrapMode.Clamp;
            m_transmittanceLUT.filterMode = FilterMode.Bilinear;
            m_transmittanceLUT.name = "TransmittanceLUT";

            m_multiscatteringMat = new Material(setting.MultiscatteringShader);
            m_multiscatteringLUT = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat);
            m_multiscatteringLUT.wrapMode = TextureWrapMode.Clamp;
            m_multiscatteringLUT.filterMode = FilterMode.Bilinear;
            m_multiscatteringLUT.name = "MultiscatteringLUT";

            m_skyviewMat = new Material(setting.SkyviewShader);
            m_skyviewLUT = new RenderTexture(256, 128, 0, RenderTextureFormat.ARGBFloat);
            m_skyviewLUT.wrapMode = TextureWrapMode.Clamp;
            m_skyviewLUT.filterMode = FilterMode.Bilinear;
            m_skyviewLUT.name = "SkyviewLUT";

            m_aerialPerspectiveLUTMat = new Material(setting.AerialPerspectiveLUTShader);
            m_aerialPerspectiveLUT = new RenderTexture(32 * 32, 32, 0, RenderTextureFormat.ARGBFloat);
            m_aerialPerspectiveLUT.wrapMode = TextureWrapMode.Clamp;
            m_aerialPerspectiveLUT.filterMode = FilterMode.Bilinear;
            m_aerialPerspectiveLUT.name = "AerialPerspectiveLUT";

            m_aerialPerspectiveMat = new Material(setting.AerialPerspectiveShader);

            // 用于处理大气雾，double buffer
            /*if (m_screenRTCopy != null && m_screenRTCopy != Camera.main.activeTexture)
                m_screenRTCopy.Release();
            m_screenRTCopy = new RenderTexture(Camera.main.targetTexture);
            m_screenRTCopy.name = "GameviewWithAerialPerspectivePostEffect";*/
        }

        public void Update(CommandBuffer cmd)
        {
            BakeTransmittanceLUT(cmd);
            BakeMultiscatteringLUT(cmd);
            BakeSkyviewLUT(cmd);
            BakeAerialPerspectiveLUT(cmd);

            // 烘焙完，绑定LUT结果
            m_skyboxMat.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            m_skyboxMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            m_skyboxMat.SetTexture(ShaderProperties._SkyviewLUT, m_skyviewLUT);

            SetupAtmosphereParameters(m_skyboxMat);
        }

        public void DrawSkybox(ScriptableRenderContext context, Camera cam, CommandBuffer cmd)
        {
            Update(cmd);

            // 在lit pass之后执行
            // 保存cam buffer引用
            RenderTexture oldCamBuffer = cam.targetTexture;
            // apply大气雾，把相机buffer变成大气雾的
            //cmd.Blit(cam.activeTexture, m_screenRTCopy, m_aerialPerspectiveMat);
            //cam.SetTargetBuffers(m_screenRTCopy.colorBuffer, m_screenRTCopy.depthBuffer);
            // 保存旧相机buffer引用
            //m_screenRTCopy = oldCamBuffer;
            

            //此时是RT是skyviewLUT，应该切回Gameview RT
            cmd.SetRenderTarget(cam.activeTexture);

            context.ExecuteCommandBuffer(cmd);

            RenderSettings.skybox = m_skyboxMat;
            context.DrawSkybox(cam);
        }

        public void BakeTransmittanceLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_transmittanceMat);
            cmd.Blit(null, m_transmittanceLUT, m_transmittanceMat);
        }

        public void BakeMultiscatteringLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_multiscatteringMat);
            m_multiscatteringMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            cmd.Blit(null, m_multiscatteringLUT, m_multiscatteringMat);
        }

        public void BakeSkyviewLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_skyviewMat);
            m_skyviewMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            m_skyviewMat.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.Blit(null, m_skyviewLUT, m_skyviewMat);
        }
        public void BakeAerialPerspectiveLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_aerialPerspectiveLUTMat);
            m_aerialPerspectiveLUTMat.SetFloat(ShaderProperties._AerialPerspectiveDistance, m_setting._AerialPerspectiveDistance);
            m_aerialPerspectiveLUTMat.SetVector(ShaderProperties._AerialPerspectiveVoxelSize, new Vector4(32, 32, 32, 0));
            m_aerialPerspectiveLUTMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            m_aerialPerspectiveLUTMat.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.Blit(null, m_aerialPerspectiveLUT, m_aerialPerspectiveLUTMat);
        }

        private void SetupAtmosphereParameters(Material mat)
        {
            mat.SetFloat(ShaderProperties._SeaLevel, m_setting._SeaLevel);
            mat.SetFloat(ShaderProperties._PlanetRadius, m_setting._PlanetRadius);
            mat.SetFloat(ShaderProperties._AtmosphereHeight, m_setting._AtmosphereHeight);
            mat.SetFloat(ShaderProperties._SunLightIntensity, m_setting._SunLightIntensity);
            mat.SetColor(ShaderProperties._SunLightColor, m_setting._SunLightColor);
            mat.SetFloat(ShaderProperties._SunDiskAngle, m_setting._SunDiskAngle);
            mat.SetFloat(ShaderProperties._RayleighScatteringScale, m_setting._RayleighScatteringScale);
            mat.SetFloat(ShaderProperties._RayleighScatteringScalarHeight, m_setting._RayleighScatteringScalarHeight);
            mat.SetFloat(ShaderProperties._MieScatteringScale, m_setting._MieScatteringScale);
            mat.SetFloat(ShaderProperties._MieAnisotropy, m_setting._MieAnisotropy);
            mat.SetFloat(ShaderProperties._MieScatteringScalarHeight, m_setting._MieScatteringScalarHeight);
            mat.SetFloat(ShaderProperties._OzoneAbsorptionScale, m_setting._OzoneAbsorptionScale);
            mat.SetFloat(ShaderProperties._OzoneLevelCenterHeight, m_setting._OzoneLevelCenterHeight);
            mat.SetFloat(ShaderProperties._OzoneLevelWidth, m_setting._OzoneLevelWidth);
        }

        private static class ShaderProperties
        {
            public static readonly int _SeaLevel = Shader.PropertyToID("_SeaLevel");
            public static readonly int _PlanetRadius = Shader.PropertyToID("_PlanetRadius");
            public static readonly int _AtmosphereHeight = Shader.PropertyToID("_AtmosphereHeight");
            public static readonly int _SunLightIntensity = Shader.PropertyToID("_SunLightIntensity");
            public static readonly int _SunLightColor = Shader.PropertyToID("_SunLightColor");
            public static readonly int _SunDiskAngle = Shader.PropertyToID("_SunDiskAngle");
            public static readonly int _RayleighScatteringScale = Shader.PropertyToID("_RayleighScatteringScale");
            public static readonly int _RayleighScatteringScalarHeight = Shader.PropertyToID("_RayleighScatteringScalarHeight");
            public static readonly int _MieScatteringScale = Shader.PropertyToID("_MieScatteringScale");
            public static readonly int _MieAnisotropy = Shader.PropertyToID("_MieAnisotropy");
            public static readonly int _MieScatteringScalarHeight = Shader.PropertyToID("_MieScatteringScalarHeight");
            public static readonly int _OzoneAbsorptionScale = Shader.PropertyToID("_OzoneAbsorptionScale");
            public static readonly int _OzoneLevelCenterHeight = Shader.PropertyToID("_OzoneLevelCenterHeight");
            public static readonly int _OzoneLevelWidth = Shader.PropertyToID("_OzoneLevelWidth");

            public static readonly int _AerialPerspectiveDistance = Shader.PropertyToID("_AerialPerspectiveDistance");
            public static readonly int _AerialPerspectiveVoxelSize = Shader.PropertyToID("_AerialPerspectiveVoxelSize");

            // LUTs
            public static readonly int _TransmittanceLUT = Shader.PropertyToID("_TransmittanceLUT");
            public static readonly int _MultiscatteringLUT = Shader.PropertyToID("_MultiscatteringLUT");
            public static readonly int _SkyviewLUT = Shader.PropertyToID("_SkyviewLUT");
        }

        [System.Serializable]
        public class AtmosphereSettings
        {
            [SerializeField] public float _SeaLevel = 0.0f;
            [SerializeField] public float _PlanetRadius = 6360000.0f;
            [SerializeField] public float _AtmosphereHeight = 60000.0f;
            [SerializeField] public float _SunLightIntensity = 31.4f;
            [SerializeField] public Color _SunLightColor = Color.white;
            [SerializeField] public float _SunDiskAngle = 1.0f;
            [SerializeField] public float _RayleighScatteringScale = 1.0f;
            [SerializeField] public float _RayleighScatteringScalarHeight = 8000.0f;
            [SerializeField] public float _MieScatteringScale = 1.0f;
            [SerializeField] public float _MieAnisotropy = 0.8f;
            [SerializeField] public float _MieScatteringScalarHeight = 1200.0f;
            [SerializeField] public float _OzoneAbsorptionScale = 1.0f;
            [SerializeField] public float _OzoneLevelCenterHeight = 25000.0f;
            [SerializeField] public float _OzoneLevelWidth = 15000.0f;

            [SerializeField] public float _AerialPerspectiveDistance = 32000.0f;

            [SerializeField]
            private Shader m_proceduralSkyboxShader;
            public Shader ProceduralSkyboxShader
            {
                get
                {
                    return m_proceduralSkyboxShader;
                }
            }

            [SerializeField]
            private Shader m_transmittanceShader;
            public Shader TransmittanceShader
            {
                get
                {
                    return m_transmittanceShader;
                }
            }

            [SerializeField]
            private Shader m_multiscatteringShader;
            public Shader MultiscatteringShader
            {
                get
                {
                    return m_multiscatteringShader;
                }
            }

            [SerializeField]
            private Shader m_skyviewShader;
            public Shader SkyviewShader
            {
                get
                {
                    return m_skyviewShader;
                }
            }

            [SerializeField]
            private Shader m_aerialPerspectiveLUTShader;
            public Shader AerialPerspectiveLUTShader
            {
                get
                {
                    return m_aerialPerspectiveLUTShader;
                }
            }

            [SerializeField]
            private Shader m_aerialPerspectiveShader;
            public Shader AerialPerspectiveShader
            {
                get
                {
                    return m_aerialPerspectiveShader;
                }
            }
        }
    }
}