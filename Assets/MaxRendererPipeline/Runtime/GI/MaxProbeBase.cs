using System.Collections;
using System.IO;
using UnityEngine;

namespace MaxSRP
{
    public class MaxProbeBase
    {
        protected Cubemap m_GroundTruthEnvMap;
        protected Cubemap m_BakedEnvMap;

        public MaxProbeBase(Cubemap groundTruthEnvMap)
        {
            m_GroundTruthEnvMap = groundTruthEnvMap;
        }

        public virtual void Bake()
        {

        }

        public virtual void Submit()
        {

        }

        public virtual void Clear()
        {
            if (m_BakedEnvMap != null)
                GameObject.DestroyImmediate(m_BakedEnvMap);
        }

        ~MaxProbeBase()
        {
            Clear();
        }
    }
}