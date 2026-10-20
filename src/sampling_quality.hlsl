// SPDX-License-Identifier: LGPL-2.1-or-later
// HLSL adaptation of the verified sampling-lab/dynamic-filter.js algorithm.
// EWA kernel/LUT and soft extrema follow libplacebo, revision
// 285d1cc8af2ab79b72ac662eb09736de8c12392b. Encoded RGB, not linear light.
//
// libplacebo is free software: you can redistribute it and/or modify it under
// the GNU Lesser General Public License, version 2.1 or (at your option) later.
// It is distributed WITHOUT ANY WARRANTY; without even the implied warranty
// of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See LICENSES/LGPL-2.1.txt for the complete license.
Texture2D<float4> source_texture : register(t0);
cbuffer ScaleParameters : register(b0) {
    float2 source_size;
    float2 target_size;
};
struct VertexOutput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};
float3 fetch_source(int2 p) {
    return source_texture.Load(int3(clamp(p, int2(0, 0), int2(source_size) - 1), 0)).rgb;
}
float3 bilinear(float2 p) {
    int2 base = int2(floor(p));
    float2 t = frac(p);
    return lerp(lerp(fetch_source(base), fetch_source(base + int2(1, 0)), t.x),
                lerp(fetch_source(base + int2(0, 1)), fetch_source(base + int2(1, 1)), t.x), t.y);
}
float lanczos(float x) {
    x = abs(x);
    if (x >= 3.0) return 0.0;
    if (x < 0.0000001) return 1.0;
    float p = 3.141592653589793 * x;
    return (sin(p) / p) * (sin(p / 3.0) / (p / 3.0));
}
// The separable product is the same Lanczos3 kernel as the 2-D experiment.
// Dynamic loop bounds retain the complete support even beyond 3:1 reduction.
// Do not clamp the horizontal result: its negative lobes must reach pass two.
float4 horizontal_main(VertexOutput input) : SV_TARGET {
    int2 pixel = int2(input.position.xy);
    float scale = source_size.x / target_size.x;
    float center = (pixel.x + 0.5) * scale - 0.5;
    float stretch = max(scale, 1.0);
    int first = int(floor(center - 3.0 * stretch)) + 1;
    int last = int(floor(center + 3.0 * stretch));
    float3 sum = 0.0;
    float total = 0.0;
    [loop] for (int x = first; x <= last; x++) {
        float w = lanczos((x - center) / stretch);
        sum += w * fetch_source(int2(x, pixel.y));
        total += w;
    }
    return float4(sum / total, 1.0);
}
float4 vertical_main(VertexOutput input) : SV_TARGET {
    int2 pixel = int2(input.position.xy);
    float scale = source_size.y / target_size.y;
    float center = (pixel.y + 0.5) * scale - 0.5;
    float stretch = max(scale, 1.0);
    int first = int(floor(center - 3.0 * stretch)) + 1;
    int last = int(floor(center + 3.0 * stretch));
    float3 sum = 0.0;
    float total = 0.0;
    [loop] for (int y = first; y <= last; y++) {
        float w = lanczos((y - center) / stretch);
        int2 p = int2(pixel.x, clamp(y, 0, int(source_size.y) - 1));
        sum += w * source_texture.Load(int3(p, 0)).rgb;
        total += w;
    }
    return float4(saturate(sum / total), 1.0);
}
float3 power32(float3 x) {
    x *= x; x *= x; x *= x; x *= x; x *= x;
    return x;
}
// EWA_LUT/EWA_RADIUS/EWA_FIRST are prepended by the native shader compiler.
float radial_weight(float distance) {
    if (distance >= EWA_RADIUS) return 0.0;
    float p = distance / EWA_RADIUS * 255.0;
    int i = int(floor(p));
    return lerp(EWA_LUT[i], EWA_LUT[min(i + 1, 255)], frac(p));
}
float3 ewa(float2 center) {
    int2 base = int2(floor(center));
    float2 fraction = frac(center);
    float3 sum = 0.0, dark = 0.0, light = 0.0;
    float3 dark_weight = 0.0, light_weight = 0.0;
    float total = 0.0;
    [loop] for (int y = -3; y <= 4; y++) {
        [loop] for (int x = -3; x <= 4; x++) {
            float d = length(float2(x, y) - fraction);
            if (d >= EWA_RADIUS) continue;
            float w = radial_weight(d);
            float3 c = fetch_source(base + int2(x, y));
            sum += w * c;
            total += w;
            if (d <= EWA_FIRST) {
                float3 wd = w * power32(1.1 - c);
                float3 wl = w * power32(c + 0.1);
                dark += wd * (1.0 - c);
                light += wl * c;
                dark_weight += wd;
                light_weight += wl;
            }
        }
    }
    float3 value = sum / total;
    float3 lo = 1.0 - dark / dark_weight, hi = light / light_weight;
    float3 limited = min(max(value, lo), hi);
    limited = float3(lo.r > hi.r ? (lo.r + hi.r) * 0.5 : limited.r,
                     lo.g > hi.g ? (lo.g + hi.g) * 0.5 : limited.g,
                     lo.b > hi.b ? (lo.b + hi.b) * 0.5 : limited.b);
    return saturate(lerp(value, limited, 0.8));
}
float4 upscale_main(VertexOutput input) : SV_TARGET {
    float2 center = (floor(input.position.xy) + 0.5) * source_size / target_size - 0.5;
    float3 detail = ewa(center);
    float3 base = bilinear(center);
    const float3 luma = float3(0.2126, 0.7152, 0.0722);
    if (any(detail != detail.ggg) || any(base != base.ggg))
        detail = saturate(dot(detail, luma) + base - dot(base, luma));
    return float4(detail, 1.0);
}
float4 identity_main(VertexOutput input) : SV_TARGET {
    return source_texture.Load(int3(int2(input.position.xy), 0));
}
