using UnityEngine;

namespace MaxSRP
{
    [CreateAssetMenu(menuName = "MaxSRP/Shadow Configuration")]
    public sealed class MaxShadowConfig : ScriptableObject
    {
        [SerializeField] private Shader m_tileClearShader;
        public Shader TileClearShader => m_tileClearShader;
        [Header("PC Profile")]
        [SerializeField] private CascadeSettings m_pc = new CascadeSettings();
        [Header("Mobile Profile")]
        [SerializeField] private CascadeSettings m_mobile = new CascadeSettings();

        public CascadeSettings GetProfile(MaxRenderPlatform platform) =>
            platform == MaxRenderPlatform.Mobile ? m_mobile : m_pc;

        private void OnValidate()
        {
            if (m_pc == null) m_pc = new CascadeSettings();
            if (m_mobile == null) m_mobile = new CascadeSettings();
            MaxRenderQuality.NotifyChanged(this);
        }
    }
}
