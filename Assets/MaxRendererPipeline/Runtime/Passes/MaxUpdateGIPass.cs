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
            skyboxReflectionProbe = new MaxReflectionProbeSkybox(cubemap, cs);
        }

        public void BakeAndSubmit() 
        {
            skyboxLightProbe.Bake();
            skyboxLightProbe.Submit();

            skyboxReflectionProbe.Bake();
            skyboxReflectionProbe.Submit();
        }
    }
}