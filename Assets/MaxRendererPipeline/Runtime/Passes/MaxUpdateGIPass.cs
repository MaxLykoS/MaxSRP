using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    public class MaxIBLGIPass
    {
        private MaxLightProbeSkybox skyboxLightProbe;
        private MaxReflectionProbeSkybox skyboxReflectionProbe;

        public MaxIBLGIPass(Cubemap cubemap, ComputeShader cs)
        {
            skyboxLightProbe = new MaxLightProbeSkybox(cubemap, cs);
            skyboxLightProbe.Bake();
            skyboxLightProbe.Submit();

            skyboxReflectionProbe = new MaxReflectionProbeSkybox(cubemap, cs);
            skyboxReflectionProbe.Bake();
            skyboxReflectionProbe.Submit();
        }
    }
}