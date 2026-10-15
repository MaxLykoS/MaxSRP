using System;
using UnityEngine;

namespace MaxSRP
{
    public class MaxProbeBase : IDisposable
    {
        protected Cubemap m_GroundTruthEnvMap;
        protected Cubemap m_BakedEnvMap;

        public MaxProbeBase(Cubemap groundTruthEnvMap)
        {
            m_GroundTruthEnvMap = groundTruthEnvMap;
        }

        public virtual void Bake() { }
        public virtual void Submit() { }

        public virtual void Clear()
        {
            MaxRenderResources.Destroy(ref m_BakedEnvMap);
        }

        public void Dispose()
        {
            Clear();
        }
    }
}