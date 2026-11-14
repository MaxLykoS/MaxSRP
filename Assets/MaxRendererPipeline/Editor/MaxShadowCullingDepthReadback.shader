Shader "Hidden/MaxSRP/Shadow Culling Depth Readback"
{
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment Frag
            #include "UnityCG.cginc"
            Texture2D<float> _ShadowCullingSource;
            float4 _ShadowCullingSourceSize;
            float Frag(v2f_img input) : SV_Target
            {
                int2 pixel = int2(input.pos.xy);
                // Atlas outer corners are outside all active cascade tiles.
                // These values calibrate CPU readback row orientation only.
                if (pixel.x == 0 && pixel.y == 0) return -1000.0;
                if (pixel.x == 0 && pixel.y == (int)_ShadowCullingSourceSize.y - 1) return -2000.0;
                return _ShadowCullingSource.Load(int3(pixel, 0));
            }
            ENDHLSL
        }
    }
}
