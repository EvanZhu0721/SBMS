// SPDX-License-Identifier: LGPL-2.1-or-later
// Original SBMS selective defringe algorithm, HLSL port of
// sampling-lab/dynamic-filter.js refine=2; clamp at display boundaries.
// HLSL globals are uniform unless static; this must be a compile-time constant.
static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
float saturation(float3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }
float orderError(float3 c) {
  return max(0.0, max(min(c.r, c.b) - c.g, c.g - max(c.r, c.b))) / (saturation(c) + 0.00001);
}
float pairReversal(float3 a, float3 b) {
  float within = abs(a.r-a.g) + abs(a.g-a.b) + abs(b.r-b.g) + abs(b.g-b.b);
  float rgb = within + abs(a.b-b.r), bgr = within + abs(a.r-b.b);
  return min(max(0.0, rgb-abs(b.b-a.r))/(rgb+0.00001),
             max(0.0, bgr-abs(b.r-a.b))/(bgr+0.00001));
}
float refinementGate(int2 p, int2 size, float3 center) {
  // Gray subpixel coverage traverses R/G/B (or B/G/R) in order. A repeated
  // colored plateau does not. Coherent neighbors protect quantized color-band
  // boundaries without propagating a veto across unrelated glyph edges.
  float3 direction = center - dot(center, ((float3)(1.0/3.0)));
  float magnitude = length(direction);
  float order = orderError(center), reversal = 0.0;
  [loop] for (int y = -1; y <= 1; y++) {
    [loop] for (int x = -1; x <= 1; x++) {
      if (x == 0 && y == 0) continue;
      int2 q = p + int2(x,y);
      float3 neighbor = fetch_source(q);
      float3 other = neighbor - dot(neighbor, ((float3)(1.0/3.0)));
      float otherMagnitude = length(other);
      if (otherMagnitude < magnitude * 0.25) continue;
      if (dot(direction, other) <= 0.95 * magnitude * otherMagnitude) continue;
      order = max(order, orderError(neighbor) *
        smoothstep(0.02, 0.08, saturation(neighbor)));
      if (y == 0) reversal = max(reversal,
        x < 0 ? pairReversal(neighbor, center) : pairReversal(center, neighbor));
    }
  }
  return (1.0 - smoothstep(0.0025, 0.01, order)) *
         (1.0 - smoothstep(0.0025, 0.01, reversal));
}
float strokeConfidence(int2 p, int2 size) {
  float3 colors[9];
  float light[9], neutral[9];
  [loop] for (int i = 0; i < 9; i++) {
    int2 q = p + int2(i - 4, 0);
    colors[i] = fetch_source(q);
    light[i] = dot(colors[i], LUMA);
    neutral[i] = 1.0 - smoothstep(0.02, 0.08, saturation(colors[i]));
  }
  float confidence = 0.0;
  [loop] for (int l = 0; l < 4; l++) {
    [loop] for (int r = 5; r < 9; r++) {
      float anchors = neutral[l] * neutral[r] *
        (1.0 - smoothstep(0.03, 0.12, abs(light[l] - light[r])));
      if (anchors <= 0.0) continue;
      float background = (light[l] + light[r]) * 0.5;
      bool dark = light[4] < background;
      float direction = dark ? 1.0 : -1.0;
      float extreme = dark ? 1.0 : 0.0;
      int core = l + 1;
      [loop] for (int j = 0; j < 9; j++) {
        if (j < l || j > r) continue;
        extreme = dark ? min(extreme, min(colors[j].r, min(colors[j].g, colors[j].b))) :
                         max(extreme, max(colors[j].r, max(colors[j].g, colors[j].b)));
        if (j > l && j < r && direction * light[j] < direction * light[core]) core = j;
      }
      // A gray subpixel stroke makes one valley (or peak) when channels are
      // traversed in physical RGB/BGR order. Repeated real color bands fold
      // back and fail this check even when their endpoints are neutral.
      float error = 1.0;
      [loop] for (int order = 0; order < 2; order++) {
        float first = order == 0 ? colors[l].r : colors[l].b;
        float last = order == 0 ? colors[r].b : colors[r].r;
        float previous = first, variation = 0.0;
        [loop] for (int j = 0; j < 9; j++) {
          if (j < l || j > r) continue;
          float3 value = order == 0 ? colors[j] : colors[j].bgr;
          variation += abs(value.r - previous);
          variation += abs(value.g - value.r);
          variation += abs(value.b - value.g);
          previous = value.b;
        }
        error = min(error, max(0.0, variation - abs(first - extreme) -
          abs(last - extreme)) / (variation + 0.00001));
      }
      float model = (1.0 - smoothstep(0.0025, 0.01, error)) *
        smoothstep(0.1, 0.35, direction * (background - extreme));
      if (model <= 0.0) continue;
      float support = 0.0;
      [loop] for (int side = 0; side < 2; side++) {
        [loop] for (int distance = 1; distance <= 16; distance++) {
          bool connected = false;
          [loop] for (int dx = -1; dx <= 1; dx++) {
            int2 q = p + int2(core - 4 + dx, (side == 0 ? -1 : 1) * distance);
            float3 neighbor = fetch_source(q);
            float value = dot(neighbor, LUMA);
            if (direction * (background - value) > 0.1) connected = true;
            float evidence = (1.0 - smoothstep(0.02, 0.08, saturation(neighbor))) *
              (1.0 - smoothstep(0.015, 0.08, abs(value - extreme)));
            support = max(support, evidence);
          }
          if (!connected || support >= 1.0) break;
        }
      }
      confidence = max(confidence, anchors * model * support);
    }
  }
  return confidence;
}
float4 defringe_main(VertexOutput input) : SV_TARGET {
  // Preserve texture row order: the regular presentation shader handles Y.
  int2 p = int2(input.position.xy);
  int2 size = int2(source_size);
  float4 original = source_texture.Load(int3(p, 0));
  if (saturation(original.rgb) == 0.0) { return original; }
  float3 colors[7];
  float light[7];
  float neutral[7];
  [loop] for (int i = 0; i < 7; i++) {
    int2 q = int2(p.x + i - 3, p.y);
    colors[i] = fetch_source(q);
    light[i] = dot(colors[i], LUMA);
    neutral[i] = 1.0 - smoothstep(0.02, 0.08, saturation(colors[i]));
  }
  float3 chroma = original.rgb - light[3];
  float magnitude = length(chroma);
  float confidence = 0.0;
  [loop] for (int l = 0; l < 3; l++) {
    [loop] for (int r = 4; r < 7; r++) {
      float anchors = neutral[l] * neutral[r];
      if (anchors <= 0.0) continue;
      float lo = min(light[l], light[r]), hi = max(light[l], light[r]);
      float inRange = 1.0 - smoothstep(0.015, 0.08, max(lo - light[3], light[3] - hi));
      float edge = smoothstep(0.1, 0.35, hi - lo) * inRange;
      // A thin stroke may have no gray/black interior. Require a second,
      // opposite chroma sample between near-equal neutral background anchors.
      // A lone colored line on a flat gray background therefore stays intact.
      float sameBackground = 1.0 - smoothstep(0.03, 0.12, hi - lo);
      float background = (light[l] + light[r]) * 0.5;
      float excursion = light[3] - background;
      float pairEvidence = 0.0;
      if (sameBackground > 0.0) {
        [loop] for (int j = 1; j < 6; j++) {
          if (j <= l || j >= r || j == 3) continue;
          float3 other = colors[j] - light[j];
          float otherMagnitude = length(other);
          if (otherMagnitude < 0.000001 || excursion * (light[j] - background) <= 0.0) continue;
          float opposition = -dot(chroma, other) / (magnitude * otherMagnitude);
          float balance = min(magnitude, otherMagnitude) / max(magnitude, otherMagnitude);
          float stroke = smoothstep(0.1, 0.35, min(abs(excursion), abs(light[j] - background)));
          pairEvidence = max(pairEvidence, stroke *
            smoothstep(0.55, 0.92, opposition) *
            smoothstep(0.12, 0.5, balance));
        }
      }
      confidence = max(confidence, anchors * max(edge, sameBackground * pairEvidence));
    }
  }
  if (2 == 2) {
    float gate = refinementGate(p, size, original.rgb);
    // Only seek extra evidence for visibly colored pixels not already handled
    // with full confidence. The existing real-color veto remains mandatory.
    if (gate > 0.0 && confidence < 1.0 && saturation(original.rgb) >= 0.08)
      confidence = max(confidence, strokeConfidence(p, size));
    confidence *= gate;
  } else if (2 != 0 && confidence > 0.0) confidence *= refinementGate(p, size, original.rgb);
  // Mixing with the same Y' cannot overshoot the RGB gamut and preserves Y'
  // before the unavoidable RGBA8 storage quantization. No brightness blur.
  return float4(lerp(original.rgb, ((float3)(light[3])), 0.96 * confidence), original.a);
}
