#ifndef DIRECT_DELTA_LIGHTING_INCLUDED
#define DIRECT_DELTA_LIGHTING_INCLUDED

// 阅读第4步：对已混合好的表面做光照。整段是工程实现，不是PPT原文。

float3 DDLightSurface(DDSurface surface, float3 worldPosition)
{
    // 下面是本工程简化GGX直射光；不是UE光照源码或PPT提供的实现。
    // roughness图保持原值，只有BRDF计算时设0.06下限，以避免高光奇点。
    const float pi = 3.14159265359;
    float3 normal = surface.normal;
    float3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - worldPosition + float3(0,1e-6,0));
    float3 lightDirection = normalize(_DDLightDirection.xyz + float3(0,1e-6,0));
    float3 halfDirection = normalize(viewDirection + lightDirection + float3(0,1e-6,0));
    float nl = saturate(dot(normal,lightDirection)), nv = max(saturate(dot(normal,viewDirection)), .001);
    float nh = saturate(dot(normal,halfDirection)), vh = saturate(dot(viewDirection,halfDirection));
    // Dielectric terrain: GGX distribution, Schlick Fresnel and Smith's
    // Schlick-GGX masking approximation. No metallic input in the two-array ABI.
    // perceptual roughness²得到GGX alpha，alpha²进入法线分布函数。
    float roughness = max(surface.roughness, .06);
    float alpha = roughness * roughness, alphaSquared = alpha * alpha;
    float denominator = nh * nh * (alphaSquared - 1) + 1;
    float distribution = alphaSquared / max(pi * denominator * denominator, 1e-6);
    // Schlick-GGX几何遮蔽 + Schlick Fresnel（非金属F0=0.04）。
    float k = (roughness + 1) * (roughness + 1) * .125;
    float masking = nv / (nv * (1-k) + k) * nl / max(nl * (1-k) + k, 1e-5);
    float fresnelPower = pow(1-vh,5);
    float3 fresnel = .04 + .96 * fresnelPower;
    float3 specular = distribution * masking * fresnel / max(4 * nv * nl, 1e-4);
    float3 diffuse = (1-fresnel) * surface.albedo / pi;
    float3 direct = (diffuse + specular) * nl * _DDLightColor.rgb;
    // 工程自定义ambient：AO仅乘环境项，朝上的法线更亮；无阴影/间接光缓存。
    float hemisphere = .35 + .65 * saturate(normal.y);
    float3 ambient = surface.albedo * _DDAmbientColor.rgb * surface.ao * hemisphere;
    return max(direct + ambient, 0);
}


#endif
