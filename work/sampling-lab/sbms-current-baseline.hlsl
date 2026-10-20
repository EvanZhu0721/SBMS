
Texture2D source_texture : register(t0);
SamplerState source_sampler : register(s0);

cbuffer ScaleParameters : register(b0) {
    float2 source_size;
    float2 target_size;
};

struct VertexOutput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VertexOutput vertex_main(uint id : SV_VertexID) {
    float2 positions[3] = {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };
    VertexOutput output;
    output.position = float4(positions[id], 0.0, 1.0);
    output.uv = float2(
        (positions[id].x + 1.0) * 0.5,
        (1.0 - positions[id].y) * 0.5);
    return output;
}

void area_axis(
    float first,
    float last,
    float source_extent,
    out float2 position,
    out float2 weight) {
    float base = floor(first);
    float3 coverage = float3(
        max(0.0, min(last, base + 1.0) - max(first, base)),
        max(0.0, min(last, base + 2.0) - max(first, base + 1.0)),
        max(0.0, min(last, base + 3.0) - max(first, base + 2.0)));
    coverage /= last - first;

    // Two adjacent weighted texels are exactly representable by one bilinear
    // lookup. The third texel becomes the second lookup for this axis.
    weight = float2(coverage.x + coverage.y, coverage.z);
    float blend = weight.x > 0.0 ? coverage.y / weight.x : 0.0;
    position = float2(base + 0.5 + blend, base + 2.5) / source_extent;
}

float4 exact_area(float2 target_pixel) {
    float2 scale = source_size / target_size;
    float2 first = target_pixel * scale;
    float2 last = (target_pixel + 1.0) * scale;
    float2 position_x;
    float2 position_y;
    float2 weight_x;
    float2 weight_y;
    area_axis(first.x, last.x, source_size.x, position_x, weight_x);
    area_axis(first.y, last.y, source_size.y, position_y, weight_y);

    return
        source_texture.SampleLevel(source_sampler, float2(position_x.x, position_y.x), 0.0)
            * weight_x.x * weight_y.x +
        source_texture.SampleLevel(source_sampler, float2(position_x.y, position_y.x), 0.0)
            * weight_x.y * weight_y.x +
        source_texture.SampleLevel(source_sampler, float2(position_x.x, position_y.y), 0.0)
            * weight_x.x * weight_y.y +
        source_texture.SampleLevel(source_sampler, float2(position_x.y, position_y.y), 0.0)
            * weight_x.y * weight_y.y;
}

float3 suppress_subpixel_fringe(float2 target_pixel, float3 area_color) {
    float2 center_uv = (target_pixel + 0.5) / target_size;
    float2 half_source_pixel = float2(0.5 / source_size.x, 0.0);
    float3 left = source_texture.SampleLevel(
        source_sampler, center_uv - half_source_pixel, 0.0).rgb;
    float3 right = source_texture.SampleLevel(
        source_sampler, center_uv + half_source_pixel, 0.0).rgb;
    float3 chroma_low_pass = (left + 2.0 * area_color + right) * 0.25;

    // ClearType encodes coverage in RGB subpixels. After a non-integer resize
    // that high-frequency chroma is no longer aligned with the physical panel.
    // Preserve the area-filtered luminance and only borrow low-passed chroma
    // where the wider neighborhood is close to neutral. Solid UI colors keep
    // their original chroma.
    const float3 luma_weights = float3(0.2126, 0.7152, 0.0722);
    float area_luma = dot(area_color, luma_weights);
    float low_pass_luma = dot(chroma_low_pass, luma_weights);
    float3 area_chroma = area_color - area_luma;
    float3 low_pass_chroma = chroma_low_pass - low_pass_luma;
    float neighborhood_saturation =
        max(chroma_low_pass.r, max(chroma_low_pass.g, chroma_low_pass.b)) -
        min(chroma_low_pass.r, min(chroma_low_pass.g, chroma_low_pass.b));
    float chroma_delta = length(area_chroma - low_pass_chroma);
    float neutral_gate = 1.0 - smoothstep(0.04, 0.18, neighborhood_saturation);
    float artifact_gate = saturate(chroma_delta * 8.0) * neutral_gate * 0.85;
    float3 filtered_chroma = lerp(area_chroma, low_pass_chroma, artifact_gate);
    return saturate(area_luma + filtered_chroma);
}

float4 pixel_main(VertexOutput input) : SV_TARGET {
    float2 scale = source_size / target_size;
    bool area_supported =
        scale.x > 1.0001 && scale.x <= 2.0 &&
        scale.y > 1.0001 && scale.y <= 2.0;
    if (area_supported) {
        float2 target_pixel = floor(input.position.xy);
        float4 area = exact_area(target_pixel);
        area.rgb = suppress_subpixel_fringe(target_pixel, area.rgb);
        return area;
    }
    return source_texture.Sample(source_sampler, input.uv);
}
