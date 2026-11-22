Shader "MaxTerrain/GotGrass"
{
    Properties
    {
        _GoTColorRamp ("Blade ramp", 2D) = "white" {}
        _GoTColorDetail ("Clump and blade colour", 2D) = "gray" {}
        _GoTScalarRamp ("Blade smoothness", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "GotGrassGBuffer"
            Tags { "LightMode"="MaxDeferred" }
            Cull Off ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex GrassVertex
            #pragma fragment GrassFragment
            #include "GotGrassGeometry.hlsl"

            float4x4 _MaxRasterVP;
            float _GoTColorDetailFlipY;
            Texture2D<float4> _GoTColorRamp,_GoTColorDetail,_GoTScalarRamp;
            SamplerState sampler_GoTColorRamp,sampler_GoTColorDetail,sampler_GoTScalarRamp;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 normal : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float2 material : TEXCOORD2;
                nointerpolation uint species : TEXCOORD3;
            };

            Varyings GrassVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
            {
                Varyings result;
                float3 world;
                GotGrassVertex(vertex,instance,world,result.normal,result.uv,result.material,result.species);
                result.position=mul(_MaxRasterVP,float4(world,1));
                return result;
            }

            struct GBuffer
            {
                float4 albedo : SV_Target0;
                float4 normal : SV_Target1;
                float4 attributes : SV_Target2;
                float4 bakedAndAo : SV_Target3;
            };

            GBuffer GrassFragment(Varyings input,bool front:SV_IsFrontFace)
            {
                SPGoTType type=_GoTTypes[input.species];
                float t=abs(input.uv.y);
                float2 rampUv=float2(input.uv.x,.5);
                float3 ramp=_GoTColorRamp.Sample(sampler_GoTColorRamp,rampUv).rgb;
                float detailT=_GoTColorDetailFlipY>.5?1-t:t;
                float3 detail=_GoTColorDetail.SampleLevel(sampler_GoTColorDetail,float2(input.material.x,detailT),0).rgb;
                float3 albedo=lerp(2*ramp*detail,1-2*(1-ramp)*(1-detail),step(.5,detail));
                float smoothness=_GoTScalarRamp.Sample(sampler_GoTScalarRamp,rampUv).r*type.v[180/4];
                float baseAo=type.v[192/4]+.01*input.material.y*(type.v[196/4]-type.v[192/4]);
                float tip=min(t+.300000011920929,1);
                float ao=lerp(baseAo,1,tip*tip);
                float3 normal=normalize(front==(input.uv.y>0)?input.normal:-input.normal);

                GBuffer result;
                result.albedo=float4(saturate(albedo),1);
                // Deformed foliage has no previous-frame positions. A zero tag
                // rejects camera-only TAA reprojection on these depth pixels.
                result.normal=float4(normal*.5+.5,0);
                result.attributes=float4(1,0,1-saturate(smoothness),0);
                result.bakedAndAo=float4(0,0,0,saturate(ao));
                return result;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
