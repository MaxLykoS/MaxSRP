using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MaxSRP
{
    [System.Serializable]
    public class CascadeSettings
    {
        public float ShadowDistance = 50;
        public ShadowSettings Cascade0 = new ShadowSettings();
        public ShadowSettings Cascade1 = new ShadowSettings();
        public ShadowSettings Cascade2 = new ShadowSettings();
        public ShadowSettings Cascade3 = new ShadowSettings();

        public Texture2D BlueNoiseTexture;

        public void Set()
        {
            int i = 0;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade0.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade0.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssSearchRadius" + i, Cascade0.PcssSearchRadius);
            Shader.SetGlobalFloat("_PcssFilterRadius" + i, Cascade0.PcssFilterRadius);

            i = 1;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade1.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade1.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssSearchRadius" + i, Cascade1.PcssSearchRadius);
            Shader.SetGlobalFloat("_PcssFilterRadius" + i, Cascade1.PcssFilterRadius);

            i = 2;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade2.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade2.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssSearchRadius" + i, Cascade2.PcssSearchRadius);
            Shader.SetGlobalFloat("_PcssFilterRadius" + i, Cascade2.PcssFilterRadius);

            i = 3;
            Shader.SetGlobalFloat("_ShadingPointNormalBias" + i, Cascade3.ShadingPointNormalBias);
            Shader.SetGlobalFloat("_DepthNormalBias" + i, Cascade3.DepthNormalBias);
            Shader.SetGlobalFloat("_PcssSearchRadius" + i, Cascade3.PcssSearchRadius);
            Shader.SetGlobalFloat("_PcssFilterRadius" + i, Cascade3.PcssFilterRadius);
        }
    }

    [System.Serializable]
    public class ShadowSettings
    {
        public float ShadingPointNormalBias = 0.1f;
        public float DepthNormalBias = 0.005f;
        public float PcssSearchRadius = 1.0f;
        public float PcssFilterRadius = 7.0f;
    }
}
