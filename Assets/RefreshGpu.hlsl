// Full-screen D3D11 pixel passes. All working textures are linear float RGB, as in
// the CPU reference; only the native premultiplied output is quantized to BGRA8.
cbuffer Parameters : register(b0)
{
    float4 Native;       // width, height, reference elapsed milliseconds, valid
    float4 Grid;         // cell size, columns, bloom step, rows
    float4 Sizes;        // bloom width/height, spectrum work width/height
    float4 Rain;         // rain colour RGB, Gaussian spacing in source texels
    float4 StageSizes;         // target width/height, source width/height
    float4 Kernel;       // box radius, horizontal, reserved
    float4 Timing;       // block lifetime, rise, fade start, background restore (reference ms)
    float4 Reserved1;
};
Texture2D<float4> Input0 : register(t0);
Texture2D<float4> Input1 : register(t1);
Texture2D<float4> Input2 : register(t2);
Texture2D<float4> Input3 : register(t3);
Texture2D<float4> Input4 : register(t4);
Texture2D<float4> Input5 : register(t5);
Texture2D<float4> Input6 : register(t6);

float Smooth(float low, float high, float value)
{
    float t = saturate((value - low) / (high - low));
    return t * t * (3 - 2 * t);
}
float Opacity(float age)
{
    if (age <= 0 || age >= Timing.x) return 0;
    return .88 * Smooth(0, Timing.y, age) * (1 - Smooth(Timing.z, Timing.x, age));
}
float3 Saturation(float3 colour, float amount)
{
    float luma = dot(colour, float3(.2126, .7152, .0722));
    return lerp(luma.xxx, colour, amount);
}
float4 Bilinear(Texture2D<float4> image, float2 coordinate, int2 dimensions)
{
    coordinate = clamp(coordinate, 0, dimensions - 1);
    int2 p = (int2)floor(coordinate), q = min(p + 1, dimensions - 1);
    float2 w = coordinate - p;
    return lerp(lerp(image.Load(int3(p, 0)), image.Load(int3(q.x, p.y, 0)), w.x),
        lerp(image.Load(int3(p.x, q.y, 0)), image.Load(int3(q, 0)), w.x), w.y);
}
float4 EmissionPS(float4 position : SV_Position) : SV_Target
{
    int2 pixel = (int2)position.xy;
    int stepSize = (int)Grid.z, cellSize = (int)Grid.x;
    int2 first = pixel * stepSize / cellSize;
    int2 last = min((pixel * stepSize + stepSize - 1) / cellSize, int2(Grid.y, Grid.w) - 1);
    float3 emission = 0;
    [loop] for (int y = first.y; y <= last.y; ++y)
    [loop] for (int x = first.x; x <= last.x; ++x)
    {
        float4 block = Input0.Load(int3(x, y, 0));
        float4 rect = Input1.Load(int3(x, y, 0));
        float2 overlap = max(0, min(rect.zw, (pixel + 1) * stepSize) - max(rect.xy, pixel * stepSize));
        float opacity = Opacity(Native.z - block.w);
        float3 pigment = Saturation(block.rgb, opacity / .88);
        emission += pigment * (opacity * (.75 + .25 * opacity / .88) * .8)
            * (overlap.x * overlap.y / (stepSize * stepSize));
    }
    return float4(emission, 0);
}
float4 BoxPS(float4 position : SV_Position) : SV_Target
{
    int2 pixel = (int2)position.xy, dimensions = (int2)StageSizes.zw;
    int radius = (int)Kernel.x;
    int2 axis = Kernel.y > .5 ? int2(1, 0) : int2(0, 1);
    float3 sum = 0;
    [loop] for (int offset = -radius; offset <= radius; ++offset)
    {
        int2 sampleAt = pixel + offset * axis;
        if (all(sampleAt >= 0) && all(sampleAt < dimensions)) sum += Input0.Load(int3(sampleAt, 0)).rgb;
    }
    return float4(max(0, sum / (2 * radius + 1)), 0);
}
float4 SpectrumSharpPS(float4 position : SV_Position) : SV_Target
{
    int2 pixel = (int2)position.xy;
    float4 pigment = Input0.Load(int3(pixel, 0));
    float4 parameters = Input1.Load(int3(pixel, 0));
    float envelope = Opacity(Native.z - parameters.y) / .88;
    if (envelope <= 0) return 0;
    float v = 1 - position.y / Sizes.w;
    float time = Native.z / 850;
    const float scanSpeed = (0.5 + 0.3333300054073334 * 1.5) * 3.333;
    float rainPosition = frac(v + time * parameters.x * scanSpeed / 3 + pigment.w);
    float rainAmount = 1 / rainPosition - .4 / (rainPosition + .001);
    float3 light = pigment.rgb * ((.614 * 5 * 1.4) * (Rain.rgb * rainAmount)) * envelope;
    // Same SDR/NaN handling as the reference's ClampChannel. A zero glyph at
    // the rain singularity must stay black rather than produce white pixels.
    light = float3(light.x > 0 ? min(1, light.x) : 0,
        light.y > 0 ? min(1, light.y) : 0, light.z > 0 ? min(1, light.z) : 0);
    return float4(light, 0);
}
float4 GaussianPS(float4 position : SV_Position) : SV_Target
{
    float2 centre = position.xy / StageSizes.xy * StageSizes.zw - .5;
    float2 axis = Kernel.y > .5 ? float2(Rain.w, 0) : float2(0, Rain.w);
    float3 sum = Bilinear(Input0, centre, (int2)StageSizes.zw).rgb * .208046;
    sum += (Bilinear(Input0, centre - axis, (int2)StageSizes.zw).rgb
        + Bilinear(Input0, centre + axis, (int2)StageSizes.zw).rgb) * .203926;
    sum += (Bilinear(Input0, centre - axis * 2, (int2)StageSizes.zw).rgb
        + Bilinear(Input0, centre + axis * 2, (int2)StageSizes.zw).rgb) * .192051;
    return float4(sum / (.208046 + 2 * (.203926 + .192051)), 0);
}
float3 RgbToHsl(float3 rgb)
{
    float hi = max(rgb.r, max(rgb.g, rgb.b)), lo = min(rgb.r, min(rgb.g, rgb.b));
    float lightness = (hi + lo) * .5, delta = hi - lo;
    if (delta == 0) return float3(0, 0, lightness);
    float saturation = lightness < .5 ? delta / (hi + lo) : delta / (2 - hi - lo);
    float hue = hi == rgb.r ? (rgb.g - rgb.b) / delta + (rgb.g < rgb.b ? 6 : 0)
        : hi == rgb.g ? (rgb.b - rgb.r) / delta + 2 : (rgb.r - rgb.g) / delta + 4;
    return float3(hue / 6, saturation, lightness);
}
float Hue(float p, float q, float t)
{
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1.0 / 6) return p + (q - p) * 6 * t;
    if (t < .5) return q;
    if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
    return p;
}
float3 HslToRgb(float3 hsl)
{
    if (hsl.y == 0) return hsl.zzz;
    float q = hsl.z < .5 ? hsl.z * (1 + hsl.y) : hsl.z + hsl.y - hsl.z * hsl.y;
    float p = 2 * hsl.z - q;
    return float3(Hue(p, q, hsl.x + 1.0 / 3), Hue(p, q, hsl.x), Hue(p, q, hsl.x - 1.0 / 3));
}
float4 SpectrumDeltaPS(float4 position : SV_Position) : SV_Target
{
    int2 pixel = (int2)position.xy;
    float3 source = Input0.Load(int3(pixel, 0)).rgb;
    float3 sharp = Input1.Load(int3(pixel, 0)).rgb;
    uint bw, bh; Input2.GetDimensions(bw, bh);
    float3 light = sharp + (.642 * 3 * 1.4)
        * Bilinear(Input2, position.xy / Sizes.zw * float2(bw, bh) - .5, int2(bw, bh)).rgb;
    if (all(light == 0)) return 0;
    float3 sourceHsl = RgbToHsl(source), lightHsl = RgbToHsl(light);
    if (isfinite(lightHsl.y) && abs(lightHsl.z - 1) > .001)
        light = HslToRgb(float3(sourceHsl.x, sourceHsl.y * .7 + lightHsl.y * .3, lightHsl.z));
    else
    {
        light = HslToRgb(float3(sourceHsl.x, lightHsl.y, lightHsl.z));
        lightHsl = RgbToHsl(light);
        light = HslToRgb(float3(lightHsl.x, sourceHsl.y, lightHsl.z)) * .7 + light * .3;
    }
    float3 target = saturate(source + light);
    float3 delta = max(0, target - source);
    float saturation = Opacity(Native.z - Input3.Load(int3(pixel, 0)).y) / .88;
    return float4(Saturation(delta, saturation) * (.35 * 255), 0);
}
uint Product255(uint value, uint amount)
{
    uint product = value * amount + 128;
    return (product + (product >> 8)) >> 8;
}
uint3 ProductRgb255(uint3 rgb, uint amount)
{
    return uint3(Product255(rgb.r, amount), Product255(rgb.g, amount), Product255(rgb.b, amount));
}
uint RequiredAlpha(uint source, uint target)
{
    if (target <= source || source == 255) return 0;
    return ((target - source) * 255 + 254 - source) / (255 - source);
}
uint4 AddLight(uint4 overlay, uint3 source, uint3 addition)
{
    uint3 visible = overlay.rgb + ProductRgb255(source, 255 - overlay.a);
    uint3 target = min(255, visible + addition);
    if (overlay.a == 255) return uint4(target, 255);
    uint alpha = max(overlay.a, max(RequiredAlpha(source.r, target.r),
        max(RequiredAlpha(source.g, target.g), RequiredAlpha(source.b, target.b))));
    return uint4(target - ProductRgb255(source, 255 - alpha), alpha);
}
float4 FinalPS(float4 position : SV_Position) : SV_Target
{
    if (Native.w < .5) return 0;
    int2 pixel = (int2)position.xy;
    int2 cell = pixel / (int)Grid.x;
    float4 block = Input2.Load(int3(cell, 0)), rect = Input3.Load(int3(cell, 0));
    float age = Native.z - block.w;
    float opacity = Opacity(age);
    uint amount = (uint)round(256 * (1 - Smooth(0, Timing.w, age)));
    uint4 background = (uint4)round(Input1.Load(int3(pixel, 0)) * 255);
    uint4 output = (background * amount + 128) >> 8;
    uint3 capture = (uint3)round(Input0.Load(int3(pixel, 0)).rgb * 255);
    uint alpha = (uint)round(opacity * 255);
    bool isCore = alpha != 0 && all(pixel >= rect.xy) && all(pixel < rect.zw);
    if (isCore)
    {
        float3 pigment = Saturation(block.rgb, opacity / .88);
        uint3 core = min(alpha, (uint3)round(pigment * ((.75 + .25 * opacity / .88) * alpha)));
        output = uint4(core + ProductRgb255(output.rgb, 255 - alpha),
            alpha + Product255(output.a, 255 - alpha));
    }
    else
    {
        float2 glowCoordinate = position.xy / Grid.z - .5;
        float3 glow = (Bilinear(Input4, glowCoordinate, (int2)Sizes.xy).rgb * .55
            + Bilinear(Input5, glowCoordinate, (int2)Sizes.xy).rgb * .4) * 255;
        uint3 glowBytes = (uint3)clamp(floor(glow + .5), 0, 255);
        if (any(glowBytes != 0)) output = AddLight(output, capture, glowBytes);
    }
    float3 delta = Bilinear(Input6, position.xy / Native.xy * Sizes.zw - .5, (int2)Sizes.zw).rgb;
    uint3 deltaBytes = (uint3)clamp(floor(delta + .5), 0, 255);
    if (any(deltaBytes != 0)) output = AddLight(output, capture, deltaBytes);
    return (float4)output / 255;
}
