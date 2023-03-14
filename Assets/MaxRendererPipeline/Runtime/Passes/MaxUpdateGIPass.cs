using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    public class MaxUpdateGIPass
    {
        private MaxLightProbeSkybox skyboxLightProbe;
        private MaxReflectionProbeSkybox skyboxReflectionProbe;

        public MaxUpdateGIPass()
        {
            var array1 = Resources.FindObjectsOfTypeAll<MaxLightProbeSkybox>();
            skyboxLightProbe = array1[0];

            var array2 = Resources.FindObjectsOfTypeAll<MaxReflectionProbeSkybox>();
            skyboxReflectionProbe = array2[0];

            skyboxLightProbe.ProbeInit();
            skyboxReflectionProbe.ProbeInit();
        }

        public void Execute()
        {
            skyboxLightProbe.ProbeUpdate();
            skyboxReflectionProbe.ProbeUpdate();
        }
    }
}