#ifndef MAX_UE_COLOR_TRANSFORMS_INCLUDED
#define MAX_UE_COLOR_TRANSFORMS_INCLUDED

// Independent implementation of the mathematical pipeline used by UE 5.8
// TonemapCommon.ush::FilmToneMap and PostProcessCombineLUTs.usf::ColorCorrect.
// Scene/output primaries are linear Rec.709 D65; film grading uses ACES AP1 D60.
// This implements UE's five-parameter Filmic SDR path, not its ACES 2/HDR ODT.
// Matrices below are standard primary/Bradford transforms, composed in advance.
static const float3x3 MaxSRGBToAP1 = float3x3(
    0.6130974024, 0.3395231461, 0.0473794514,
    0.0701937225, 0.9163538791, 0.0134523986,
    0.0206155929, 0.1095697729, 0.8698146341);
static const float3x3 MaxAP1ToSRGB = float3x3(
    1.7050509926, -0.6217921205, -0.0832588722,
    -0.1302564175, 1.1408047365, -0.0105483190,
    -0.0240033568, -0.1289689761, 1.1529723328);
static const float3x3 MaxAP1ToAP0 = float3x3(
    0.6954522414, 0.1406786965, 0.1638690622,
    0.0447945634, 0.8596711185, 0.0955343182,
    -0.0055258826, 0.0040252103, 1.0015006723);
static const float3x3 MaxAP0ToAP1 = float3x3(
    1.4514393161, -0.2365107469, -0.2149285693,
    -0.0765537734, 1.1762296998, -0.0996759264,
    0.0083161484, -0.0060324498, 0.9977163014);
static const float3 MaxAP1Luminance = float3(0.2722287168, 0.6740817658, 0.0536895174);

float MaxFinitePositive(float value)
{
    // Remove NaNs; retain over-range light as a finite highlight for the shoulder.
    return value == value ? clamp(value, 0.0, 1.0e20) : 0.0;
}

float3 MaxFinitePositiveColor(float3 color)
{
    return float3(MaxFinitePositive(color.r), MaxFinitePositive(color.g), MaxFinitePositive(color.b));
}

float MaxFiniteSigned(float value)
{
    return value == value ? clamp(value, -1.0e20, 1.0e20) : 0.0;
}

float3 MaxFiniteSignedColor(float3 color)
{
    return float3(MaxFiniteSigned(color.r), MaxFiniteSigned(color.g), MaxFiniteSigned(color.b));
}

// Krystek's Planckian locus, also used by UE for its temperature/tint controls.
float2 MaxPlanckUV(float temperature)
{
    float t2 = temperature * temperature;
    return float2(
        (0.860117757 + 0.000154118254 * temperature + 0.000000128641212 * t2) /
        (1.0 + 0.000842420235 * temperature + 0.000000708145163 * t2),
        (0.317398726 + 0.0000422806245 * temperature + 0.0000000420481691 * t2) /
        (1.0 - 0.0000289741816 * temperature + 0.000000161456053 * t2));
}

float2 MaxUVToXY(float2 uv)
{
    float inverse = rcp(max(2.0 * uv.x - 8.0 * uv.y + 4.0, 1.0e-5));
    return float2(3.0 * uv.x, 2.0 * uv.y) * inverse;
}

float3 MaxWhiteBalance(float3 color, float temperature, float tint)
{
    // UE's neutral control explicitly bypasses chromatic adaptation. Keep this
    // exact identity, rather than allowing CCT polynomial rounding to tint gray.
    if (abs(temperature - 6500.0) < 0.01 && abs(tint) < 1.0e-6) return color;
    temperature = clamp(temperature, 1000.0, 15000.0);
    tint = clamp(tint, -1.0, 1.0);
    float2 uv = MaxPlanckUV(temperature);
    float2 planckXY = MaxUVToXY(uv);
    // Tangent in CIE 1960 uv; tint moves along the perpendicular isotherm.
    float k = temperature * 0.001;
    float2 numerator = float2(0.860117757, 0.317398726) +
        float2(0.154118254, 0.0422806245) * k + float2(0.128641212, 0.0420481691) * k * k;
    float2 denominator = 1.0 + float2(0.842420235, -0.0289741816) * k +
        float2(0.708145163, 0.161456053) * k * k;
    float2 numeratorDerivative = float2(0.154118254, 0.0422806245) +
        2.0 * float2(0.128641212, 0.0420481691) * k;
    float2 denominatorDerivative = float2(0.842420235, -0.0289741816) +
        2.0 * float2(0.708145163, 0.161456053) * k;
    float2 tangent = normalize((numeratorDerivative * denominator - numerator * denominatorDerivative) /
        (denominator * denominator));
    float2 tintedXY = MaxUVToXY(uv + float2(tangent.y, -tangent.x) * (0.05 * tint));

    float reciprocal = rcp(temperature * (1.4388 / 1.438));
    float xDay = temperature * (1.4388 / 1.438) <= 7000.0
        ? 0.244063 + reciprocal * (99.11 + reciprocal * (2967800.0 - reciprocal * 4607000000.0))
        : 0.237040 + reciprocal * (247.48 + reciprocal * (1901800.0 - reciprocal * 2006400000.0));
    float2 daylightXY = float2(xDay, -3.0 * xDay * xDay + 2.87 * xDay - 0.275);
    float2 whiteXY = (temperature < 4000.0 ? planckXY : daylightXY) + tintedXY - planckXY;
    whiteXY.y = max(whiteXY.y, 1.0e-5);
    float3 whiteXYZ = float3(whiteXY.x, whiteXY.y, 1.0 - whiteXY.x - whiteXY.y) / whiteXY.y;
    const float3x3 rgbToXYZ = float3x3(
        0.4123907993, 0.3575843394, 0.1804807884,
        0.2126390059, 0.7151686788, 0.0721923154,
        0.0193308187, 0.1191947798, 0.9505321522);
    const float3x3 xyzToRGB = float3x3(
        3.2409699419, -1.5373831776, -0.4986107603,
        -0.9692436363, 1.8759675015, 0.0415550574,
        0.0556300797, -0.2039769589, 1.0569715142);
    const float3x3 bradford = float3x3(
        0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296);
    const float3x3 inverseBradford = float3x3(
        0.9869929, -0.1470543, 0.1599627,
        0.4323053, 0.5183603, 0.0492912,
        -0.0085287, 0.0400428, 0.9684867);
    float3 targetXYZ = float3(0.31270, 0.32900, 1.0 - 0.31270 - 0.32900) / 0.32900;
    float3 ratio = mul(bradford, targetXYZ) / max(mul(bradford, whiteXYZ), 1.0e-5);
    return mul(xyzToRGB, mul(inverseBradford, mul(bradford, mul(rgbToXYZ, color)) * ratio));
}

float3 MaxColorCorrectAP1(float3 color, float4 saturation, float4 contrast,
    float4 gamma, float4 gain, float4 offset)
{
    float luma = dot(color, MaxAP1Luminance);
    float3 saturated = max(lerp(luma.xxx, color, saturation.rgb * saturation.a), 0.0);
    // Contrast pivots around middle gray, followed by gamma, gain and offset.
    float3 contrasted = pow(saturated / 0.18, max(contrast.rgb * contrast.a, 0.01)) * 0.18;
    float3 corrected = pow(contrasted, rcp(max(gamma.rgb * gamma.a, 0.01)));
    return corrected * (gain.rgb * gain.a) + offset.rgb + offset.a;
}

float3 MaxExpandGamutAP1(float3 color)
{
    float luma = dot(color, MaxAP1Luminance);
    if (luma <= 1.0e-6) return color;
    float3 chroma = color / luma - 1.0;
    // UE's default ExpandGamut=1; neutral and dark colors remain unexpanded.
    float amount = (1.0 - exp2(-4.0 * dot(chroma, chroma))) * (1.0 - exp2(-4.0 * luma * luma));
    const float3x3 wideFromAP1 = float3x3(
        1.3704123718, -0.3292921877, -0.0636831194,
        -0.0834334917, 1.0970927480, -0.0108613795,
        -0.0257933209, -0.0986257988, 1.2036949526);
    return lerp(color, mul(wideFromAP1, color), amount);
}

float3 MaxFilmLookAP1(float3 color)
{
    float3 aces = mul(MaxAP1ToAP0, color);
    float low = min(aces.r, min(aces.g, aces.b));
    float high = max(aces.r, max(aces.g, aces.b));
    float saturation = (max(high, 1.0e-10) - max(low, 1.0e-10)) / max(high, 0.01);
    float3 differences = aces - aces.gbr;
    float chroma = sqrt(max(0.5 * dot(differences, differences), 0.0));
    float yc = (aces.r + aces.g + aces.b + 1.75 * chroma) / 3.0;
    float s = (saturation - 0.4) / 0.2;
    float shape = max(1.0 - 0.5 * abs(s), 0.0);
    shape = 0.5 * (1.0 + sign(s) * (1.0 - shape * shape));
    float glow = saturate(0.08 / max(yc, 1.0e-8) - 0.5);
    aces *= 1.0 + 0.05 * shape * glow;

    float hue = high - low > 1.0e-10
        ? atan2(1.73205080757 * (aces.g - aces.b), 2.0 * aces.r - aces.g - aces.b) : 0.0;
    // Geometric hue is already centered at red in [-pi,pi]. Neutral hue is irrelevant.
    float redWeight = smoothstep(0.0, 1.0, 1.0 - abs(hue) / 1.1780972451);
    aces.r += redWeight * redWeight * saturation * (0.03 - aces.r) * 0.18;
    float3 ap1 = max(mul(MaxAP0ToAP1, aces), 0.0);
    return lerp(dot(ap1, MaxAP1Luminance).xxx, ap1, 0.96);
}

float3 MaxFilmCurveAP1(float3 color, float4 parameters, float whiteClip)
{
    float slope = max(parameters.x, 0.01);
    float toe = clamp(parameters.y, 0.0, 1.0);
    float shoulder = clamp(parameters.z, 0.0, 1.0);
    float blackClip = clamp(parameters.w, 0.0, 1.0);
    float toeHeight = max(1.0 + blackClip - toe, 1.0e-4);
    float shoulderHeight = max(1.0 + max(whiteClip, 0.0) - shoulder, 1.0e-4);
    float toeX;
    if (toe > 0.8)
        toeX = (1.0 - toe - 0.18) / slope + log10(0.18);
    else
    {
        float match = clamp((0.18 + blackClip) / toeHeight - 1.0, -0.99999, 0.99999);
        toeX = log10(0.18) - 0.5 * log((1.0 + match) / (1.0 - match)) * toeHeight / slope;
    }
    float straightOffset = (1.0 - toe) / slope - toeX;
    float shoulderX = shoulder / slope - straightOffset;
    float3 logColor = log10(max(color, 1.0e-10));
    float3 straightValue = slope * (logColor + straightOffset);
    float3 toeCurve = -blackClip + 2.0 * toeHeight /
        (1.0 + exp(clamp((-2.0 * slope / toeHeight) * (logColor - toeX), -80.0, 80.0)));
    float3 shoulderCurve = 1.0 + whiteClip - 2.0 * shoulderHeight /
        (1.0 + exp(clamp((2.0 * slope / shoulderHeight) * (logColor - shoulderX), -80.0, 80.0)));
    toeCurve = float3(logColor.r < toeX ? toeCurve.r : straightValue.r,
        logColor.g < toeX ? toeCurve.g : straightValue.g, logColor.b < toeX ? toeCurve.b : straightValue.b);
    shoulderCurve = float3(logColor.r > shoulderX ? shoulderCurve.r : straightValue.r,
        logColor.g > shoulderX ? shoulderCurve.g : straightValue.g, logColor.b > shoulderX ? shoulderCurve.b : straightValue.b);
    float span = shoulderX - toeX;
    float3 blend = saturate((logColor - toeX) / (abs(span) > 1.0e-5 ? span : 1.0e-5));
    if (span < 0.0) blend = 1.0 - blend;
    blend = blend * blend * (3.0 - 2.0 * blend);
    float3 result = lerp(toeCurve, shoulderCurve, blend);
    return max(lerp(dot(result, MaxAP1Luminance).xxx, result, 0.93), 0.0);
}

float3 MaxUEFilmAP1(float3 color, float4 parameters, float whiteClip)
{
    // Default UE BlueCorrection=0.6, surrounding the RRT-inspired film transform.
    const float3x3 blue = float3x3(
        0.9386393778, 0.0, 0.0613606221, 0.0, 0.8307941330, 0.1692058671, 0.0, 0.0, 1.0);
    const float3x3 undoBlue = float3x3(
        1.0653748755, 0.0000014467, -0.0653710053,
        -0.0000003456, 1.2036635245, -0.2036677199,
        0.0000000198, 0.0000000212, 0.9999996001);
    color = lerp(color, mul(blue, color), 0.6);
    color = MaxFilmCurveAP1(MaxFilmLookAP1(color), parameters, whiteClip);
    return lerp(color, mul(undoBlue, color), 0.6);
}

#endif
