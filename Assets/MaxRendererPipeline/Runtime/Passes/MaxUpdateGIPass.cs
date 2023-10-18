using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    public class MaxIBLGIPass
    {
        private MaxLightProbeSkybox m_skyboxLightProbe;
        private MaxReflectionProbeSkybox m_skyboxReflectionProbe;

        public MaxIBLGIPass(Cubemap cubemap, ComputeShader cs)
        {
            m_skyboxLightProbe = new MaxLightProbeSkybox(cubemap, cs);
            m_skyboxReflectionProbe = new MaxReflectionProbeSkybox(cubemap, cs);
        }

        public void BakeAndSubmit() 
        {
            m_skyboxLightProbe.Bake();
            m_skyboxLightProbe.Submit();

            m_skyboxReflectionProbe.Bake();
            m_skyboxReflectionProbe.Submit();
        }
    }
}