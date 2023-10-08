using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    [ExecuteAlways]
    public class MaxReflectionProbeSkybox : MaxProbeBase
    {
        [SerializeField]
        public Texture2D BRDFLUT;
        public override void ProbeInit()
        {
            Shader.SetGlobalTexture("_BRDFLUT", BRDFLUT);

            RenderCubeMap();
            //SaveCubeMap();
            SubmitReflectionMap();
        }

        public void SubmitReflectionMap()
        {
            Shader.SetGlobalTexture("_IBLSpec", capturedCubemap);
        }

        public override void Clear()
        {
            base.Clear();
        }

        public override bool ProbeUpdate()
        {
            bool result = base.ProbeUpdate();
            if (!result) return false;
            RenderCubeMap();
            SubmitReflectionMap();
            return true;
        }

        private void Awake()
        {
            ProbeInit();
        }

        private void Update()
        {
            m_CurrentTimer += Time.deltaTime;
            if (m_CurrentTimer > UPDATE_INTERVAL)
            {
                ProbeUpdate();
                m_CurrentTimer = 0;
            }
        }
    }
}