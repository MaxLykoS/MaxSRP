using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    public class MaxIBLGIPass
    {
        private MaxLightProbeSkybox skyboxLightProbe;
        private MaxReflectionProbeSkybox skyboxReflectionProbe;

        public MaxIBLGIPass(Cubemap cubemap, Texture2D brdfLut, ComputeShader cs)
        {
            skyboxLightProbe = new MaxLightProbeSkybox(cubemap, cs);
            skyboxLightProbe.Bake();
            skyboxLightProbe.Submit();

            skyboxReflectionProbe = new MaxReflectionProbeSkybox(cubemap, brdfLut, cs);
        }
    }
}