using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MaxSRP
{
    [System.Serializable]
    public class ShadowSetting
    {
        [SerializeField]
        [Range(10, 500)]
        [Tooltip("最远阴影距离")]
        private float m_maxShadowDistance = 100;

        [SerializeField]
        [Range(1, 4)]
        [Tooltip("级联阴影级数")]
        private int m_shadowCascadeCount = 1;

        [SerializeField]
        [Range(1, 100)]
        [Tooltip("1级联阴影比重")]
        private float m_cascadeRatio1 = 1;

        [SerializeField]
        [Range(1, 100)]
        [Tooltip("2级联阴影比重")]
        private float m_cascadeRatio2 = 0;
        [SerializeField]
        [Range(1, 100)]
        [Tooltip("3级联阴影比重")]
        private float m_cascadeRatio3 = 0;

        [SerializeField]
        [Range(1, 100)]
        [Tooltip("4级联阴影比重")]
        private float m_cascadeRatio4 = 0;


        public int m_cascadeCount
        {
            get
            {
                return m_shadowCascadeCount;
            }
        }

        public Vector3 CascadeRatio
        {
            get
            {
                float total = m_cascadeRatio1;
                if (m_shadowCascadeCount > 1)
                {
                    total += m_cascadeRatio2;
                }
                if (m_shadowCascadeCount > 2)
                {
                    total += m_cascadeRatio3;
                }
                if (m_shadowCascadeCount > 3)
                {
                    total += m_cascadeRatio4;
                }
                return new Vector3(m_cascadeRatio1 / total, m_cascadeRatio2 / total, m_cascadeRatio3 / total);
            }
        }



        public float shadowDistance
        {
            get
            {
                return m_maxShadowDistance;
            }
        }
    }
}
