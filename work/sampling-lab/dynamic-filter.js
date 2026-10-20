/*
 * SPDX-License-Identifier: LGPL-2.1-or-later
 * WebGL/JavaScript adaptation made on 2026-10-20.
 *
 * libplacebo is free software; you can redistribute it and/or modify it under
 * the GNU Lesser General Public License, version 2.1 or (at your option) later.
 * This adaptation is distributed on the same terms, WITHOUT ANY WARRANTY;
 * without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
 * PARTICULAR PURPOSE. See LICENSE-libplacebo.txt (also embedded in dynamic.html).
 *
 * Offline sampling experiment, encoded RGB throughout.
 *
 * EWA configuration, cutoff/LUT construction and soft-extrema anti-ringing
 * follow libplacebo (LGPL-2.1-or-later), revision:
 *   285d1cc8af2ab79b72ac662eb09736de8c12392b
 *   https://github.com/haasn/libplacebo/blob/285d1cc8af2ab79b72ac662eb09736de8c12392b/src/filters.c
 *   https://github.com/haasn/libplacebo/blob/285d1cc8af2ab79b72ac662eb09736de8c12392b/src/filters.h
 *   https://github.com/haasn/libplacebo/blob/285d1cc8af2ab79b72ac662eb09736de8c12392b/src/shaders/sampling.c
 *
 * This is a WebGL mathematical port, NOT the libplacebo renderer and NOT a
 * benchmark of its native implementation. In particular, source edges repeat
 * to animate a periodic test atlas; SBMS normally clamps at the display edge.
 * There is no linear-light, sigmoid, ICC, HDR, dithering or sharpening stage.
 */
(() => {
  'use strict';
  const PI = Math.PI;
  const clip = x => Math.max(0, Math.min(1, x));
  const mix = (a, b, t) => a + (b - a) * t;
  const f32 = Math.fround;
  const ewaConfig = Object.freeze({
    name: 'ewa_lanczos4sharpest',
    radius: f32(4.2410628637960698819573),
    blur: f32(0.88451209326050047745788),
    windowRadius: f32(1.2196698912665045),
    antiring: 0.8,
    cutoff: f32(1e-3),
    lutEntries: 256,
  });
  const ewaBound = f32(ewaConfig.radius * ewaConfig.blur);
  // Experimental source-domain defringing. All thresholds refer to encoded
  // RGB/Y', not linear-light energy. This is deliberately a short horizontal
  // detector for RGB/BGR subpixel text, not a general color-denoising filter.
  const defringeConfig = Object.freeze({
    name: 'adaptive-neutral-edge-chroma',
    radius: 3,
    strength: 0.96,
    neutralLow: 0.02,
    neutralHigh: 0.08,
    contrastLow: 0.10,
    contrastHigh: 0.35,
    rangeLow: 0.015,
    rangeHigh: 0.08,
    backgroundLow: 0.03,
    backgroundHigh: 0.12,
    oppositeLow: 0.55,
    oppositeHigh: 0.92,
    balanceLow: 0.12,
    balanceHigh: 0.50,
    orderLow: 0.0025,
    orderHigh: 0.01,
    coherentCosine: 0.95,
    relativeChroma: 0.25,
  });
  // Fractional-DPI glyphs can have a colored stem with a neutral cap 13 source
  // pixels away. This bounded walk follows connected foreground, not a square
  // neighborhood; it stops as soon as the stroke disappears.
  const strokeReach = 16;

  // Jinc(x) = 2 J1(pi*x)/(pi*x). The convergent Bessel power series avoids
  // replacing the radial Jinc with a separable sinc or an unrelated cubic.
  function jinc(x) {
    const q = -((PI * x) ** 2) / 4;
    let term = 1, sum = 1;
    for (let k = 1; k < 80; k++) {
      term *= q / (k * (k + 1));
      sum += term;
      if (Math.abs(term) < 1e-16) break;
    }
    return sum;
  }
  function ewaRaw(x) {
    x = Math.abs(x);
    if (x > ewaBound) return 0;
    return jinc(x / ewaConfig.blur) *
      jinc(x / ewaBound * ewaConfig.windowRadius);
  }
  function findCutoffs() {
    const step = f32(0.01), cutoff = ewaConfig.cutoff;
    let previousX = 0, previous = f32(ewaRaw(0)), radius, first;
    for (let x = 0; x < ewaBound + step; x = f32(x + step)) {
      const value = f32(ewaRaw(x));
      if ((previous > cutoff && value <= cutoff) ||
          (previous < -cutoff && value >= -cutoff)) {
        radius = Math.min(ewaBound,
          f32(x - value * (x - previousX) / (value - previous)));
        if (first === undefined) first = radius;
      }
      previousX = x;
      previous = value;
    }
    return {radius: radius ?? ewaBound, first: first ?? ewaBound};
  }
  const cutoffs = findCutoffs();
  const ewaRadius = cutoffs.radius, ewaRadiusZero = cutoffs.first;
  const ewaLut = Float32Array.from({length: ewaConfig.lutEntries},
    (_, i) => ewaRaw(ewaRadius * i / (ewaConfig.lutEntries - 1)));
  function ewaWeight(distance) {
    if (distance >= ewaRadius) return 0;
    const position = distance / ewaRadius * (ewaLut.length - 1);
    const i = Math.floor(position);
    return mix(ewaLut[i], ewaLut[Math.min(i + 1, ewaLut.length - 1)], position - i);
  }
  function lanczos3(x) {
    x = Math.abs(x);
    if (x >= 3) return 0;
    if (x < 1e-8) return 1;
    const px = PI * x;
    return (Math.sin(px) / px) * (Math.sin(px / 3) / (px / 3));
  }
  function usesArea(sx, sy) {
    return sx > 1.0001 && sx <= 2 && sy > 1.0001 && sy <= 2;
  }
  function profile(sourceWidth, sourceHeight, outputWidth, outputHeight) {
    const sx = sourceWidth / outputWidth, sy = sourceHeight / outputHeight;
    return {
      sbms: usesArea(sx, sy) ? 'area-chroma' : 'bilinear',
      challenger: Math.abs(sx - 1) < 1e-6 && Math.abs(sy - 1) < 1e-6 ?
        'identity' : sx > 1.000001 || sy > 1.000001 ?
          'lanczos3-prefilter' : 'ewa-luma-linear-chroma',
    };
  }

  const vertexSource = `#version 300 es
precision highp float;
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;
  const defringeFragmentSource = `#version 300 es
precision highp float;
precision highp int;
uniform sampler2D u_source;
uniform vec2 u_sourceSize;
uniform int u_refine; // 0: original, 1: color protection, 2: connected thin strokes.
out vec4 outColor;
const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
float saturation(vec3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }
float orderError(vec3 c) {
  return max(0.0, max(min(c.r, c.b) - c.g, c.g - max(c.r, c.b))) / (saturation(c) + 0.00001);
}
float pairReversal(vec3 a, vec3 b) {
  float within = abs(a.r-a.g) + abs(a.g-a.b) + abs(b.r-b.g) + abs(b.g-b.b);
  float rgb = within + abs(a.b-b.r), bgr = within + abs(a.r-b.b);
  return min(max(0.0, rgb-abs(b.b-a.r))/(rgb+0.00001),
             max(0.0, bgr-abs(b.r-a.b))/(bgr+0.00001));
}
float refinementGate(ivec2 p, ivec2 size, vec3 center) {
  // Gray subpixel coverage traverses R/G/B (or B/G/R) in order. A repeated
  // colored plateau does not. Coherent neighbors protect quantized color-band
  // boundaries without propagating a veto across unrelated glyph edges.
  vec3 direction = center - dot(center, vec3(1.0/3.0));
  float magnitude = length(direction);
  float order = orderError(center), reversal = 0.0;
  for (int y = -1; y <= 1; y++) {
    for (int x = -1; x <= 1; x++) {
      if (x == 0 && y == 0) continue;
      ivec2 q = (p + ivec2(x,y) + size) % size;
      vec3 neighbor = texelFetch(u_source, q, 0).rgb;
      vec3 other = neighbor - dot(neighbor, vec3(1.0/3.0));
      float otherMagnitude = length(other);
      if (otherMagnitude < magnitude * ${defringeConfig.relativeChroma}) continue;
      if (dot(direction, other) <= ${defringeConfig.coherentCosine} * magnitude * otherMagnitude) continue;
      order = max(order, orderError(neighbor) *
        smoothstep(${defringeConfig.neutralLow}, ${defringeConfig.neutralHigh}, saturation(neighbor)));
      if (y == 0) reversal = max(reversal,
        x < 0 ? pairReversal(neighbor, center) : pairReversal(center, neighbor));
    }
  }
  return (1.0 - smoothstep(${defringeConfig.orderLow}, ${defringeConfig.orderHigh}, order)) *
         (1.0 - smoothstep(${defringeConfig.orderLow}, ${defringeConfig.orderHigh}, reversal));
}
float strokeConfidence(ivec2 p, ivec2 size) {
  vec3 colors[9];
  float light[9], neutral[9];
  for (int i = 0; i < 9; i++) {
    ivec2 q = ((p + ivec2(i - 4, 0)) % size + size) % size;
    colors[i] = texelFetch(u_source, q, 0).rgb;
    light[i] = dot(colors[i], LUMA);
    neutral[i] = 1.0 - smoothstep(${defringeConfig.neutralLow}, ${defringeConfig.neutralHigh}, saturation(colors[i]));
  }
  float confidence = 0.0;
  for (int l = 0; l < 4; l++) {
    for (int r = 5; r < 9; r++) {
      float anchors = neutral[l] * neutral[r] *
        (1.0 - smoothstep(${defringeConfig.backgroundLow}, ${defringeConfig.backgroundHigh}, abs(light[l] - light[r])));
      if (anchors <= 0.0) continue;
      float background = (light[l] + light[r]) * 0.5;
      bool dark = light[4] < background;
      float direction = dark ? 1.0 : -1.0;
      float extreme = dark ? 1.0 : 0.0;
      int core = l + 1;
      for (int j = 0; j < 9; j++) {
        if (j < l || j > r) continue;
        extreme = dark ? min(extreme, min(colors[j].r, min(colors[j].g, colors[j].b))) :
                         max(extreme, max(colors[j].r, max(colors[j].g, colors[j].b)));
        if (j > l && j < r && direction * light[j] < direction * light[core]) core = j;
      }
      // A gray subpixel stroke makes one valley (or peak) when channels are
      // traversed in physical RGB/BGR order. Repeated real color bands fold
      // back and fail this check even when their endpoints are neutral.
      float error = 1.0;
      for (int order = 0; order < 2; order++) {
        float first = order == 0 ? colors[l].r : colors[l].b;
        float last = order == 0 ? colors[r].b : colors[r].r;
        float previous = first, variation = 0.0;
        for (int j = 0; j < 9; j++) {
          if (j < l || j > r) continue;
          vec3 value = order == 0 ? colors[j] : colors[j].bgr;
          variation += abs(value.r - previous);
          variation += abs(value.g - value.r);
          variation += abs(value.b - value.g);
          previous = value.b;
        }
        error = min(error, max(0.0, variation - abs(first - extreme) -
          abs(last - extreme)) / (variation + 0.00001));
      }
      float model = (1.0 - smoothstep(${defringeConfig.orderLow}, ${defringeConfig.orderHigh}, error)) *
        smoothstep(${defringeConfig.contrastLow}, ${defringeConfig.contrastHigh}, direction * (background - extreme));
      if (model <= 0.0) continue;
      float support = 0.0;
      for (int side = 0; side < 2; side++) {
        for (int distance = 1; distance <= ${strokeReach}; distance++) {
          bool connected = false;
          for (int dx = -1; dx <= 1; dx++) {
            ivec2 q = ((p + ivec2(core - 4 + dx, (side == 0 ? -1 : 1) * distance)) % size + size) % size;
            vec3 neighbor = texelFetch(u_source, q, 0).rgb;
            float value = dot(neighbor, LUMA);
            if (direction * (background - value) > ${defringeConfig.contrastLow}) connected = true;
            float evidence = (1.0 - smoothstep(${defringeConfig.neutralLow}, ${defringeConfig.neutralHigh}, saturation(neighbor))) *
              (1.0 - smoothstep(${defringeConfig.rangeLow}, ${defringeConfig.rangeHigh}, abs(value - extreme)));
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
void main() {
  // Preserve texture row order: the regular presentation shader handles Y.
  ivec2 p = ivec2(gl_FragCoord.xy);
  ivec2 size = ivec2(u_sourceSize);
  vec4 original = texelFetch(u_source, p, 0);
  if (saturation(original.rgb) == 0.0) { outColor = original; return; }
  vec3 colors[7];
  float light[7];
  float neutral[7];
  for (int i = 0; i < 7; i++) {
    ivec2 q = ivec2((p.x + i - 3 + size.x) % size.x, p.y);
    colors[i] = texelFetch(u_source, q, 0).rgb;
    light[i] = dot(colors[i], LUMA);
    neutral[i] = 1.0 - smoothstep(${defringeConfig.neutralLow}, ${defringeConfig.neutralHigh}, saturation(colors[i]));
  }
  vec3 chroma = original.rgb - light[3];
  float magnitude = length(chroma);
  float confidence = 0.0;
  for (int l = 0; l < 3; l++) {
    for (int r = 4; r < 7; r++) {
      float anchors = neutral[l] * neutral[r];
      if (anchors <= 0.0) continue;
      float lo = min(light[l], light[r]), hi = max(light[l], light[r]);
      float inRange = 1.0 - smoothstep(${defringeConfig.rangeLow}, ${defringeConfig.rangeHigh}, max(lo - light[3], light[3] - hi));
      float edge = smoothstep(${defringeConfig.contrastLow}, ${defringeConfig.contrastHigh}, hi - lo) * inRange;
      // A thin stroke may have no gray/black interior. Require a second,
      // opposite chroma sample between near-equal neutral background anchors.
      // A lone colored line on a flat gray background therefore stays intact.
      float sameBackground = 1.0 - smoothstep(${defringeConfig.backgroundLow}, ${defringeConfig.backgroundHigh}, hi - lo);
      float background = (light[l] + light[r]) * 0.5;
      float excursion = light[3] - background;
      float pairEvidence = 0.0;
      if (sameBackground > 0.0) {
        for (int j = 1; j < 6; j++) {
          if (j <= l || j >= r || j == 3) continue;
          vec3 other = colors[j] - light[j];
          float otherMagnitude = length(other);
          if (otherMagnitude < 0.000001 || excursion * (light[j] - background) <= 0.0) continue;
          float opposition = -dot(chroma, other) / (magnitude * otherMagnitude);
          float balance = min(magnitude, otherMagnitude) / max(magnitude, otherMagnitude);
          float stroke = smoothstep(${defringeConfig.contrastLow}, ${defringeConfig.contrastHigh}, min(abs(excursion), abs(light[j] - background)));
          pairEvidence = max(pairEvidence, stroke *
            smoothstep(${defringeConfig.oppositeLow}, ${defringeConfig.oppositeHigh}, opposition) *
            smoothstep(${defringeConfig.balanceLow}, ${defringeConfig.balanceHigh}, balance));
        }
      }
      confidence = max(confidence, anchors * max(edge, sameBackground * pairEvidence));
    }
  }
  if (u_refine == 2) {
    float gate = refinementGate(p, size, original.rgb);
    // Only seek extra evidence for visibly colored pixels not already handled
    // with full confidence. The existing real-color veto remains mandatory.
    if (gate > 0.0 && confidence < 1.0 && saturation(original.rgb) >= ${defringeConfig.neutralHigh})
      confidence = max(confidence, strokeConfidence(p, size));
    confidence *= gate;
  } else if (u_refine != 0 && confidence > 0.0) confidence *= refinementGate(p, size, original.rgb);
  // Mixing with the same Y' cannot overshoot the RGB gamut and preserves Y'
  // before the unavoidable RGBA8 storage quantization. No brightness blur.
  outColor = vec4(mix(original.rgb, vec3(light[3]), ${defringeConfig.strength} * confidence), original.a);
}`;
  const fragmentSource = `#version 300 es
precision highp float;
precision highp int;
uniform sampler2D u_source;
uniform sampler2D u_cleanSource;
uniform sampler2D u_legacySource;
uniform int u_compareLegacy;
uniform int u_compareSbms;
uniform int u_comparePrevious;
uniform int u_nativePassthrough;
uniform int u_upscaleChroma;
uniform sampler2D u_ewaLut;
uniform vec2 u_sourceSize;
uniform vec2 u_outputSize;
uniform vec2 u_motion;
uniform vec2 u_viewOrigin;
uniform float u_viewZoom;
uniform float u_split;
uniform int u_mode; // 0: wipe, 1: raw HQ, 2: defringed source + identical HQ
out vec4 outColor;
const float PI = 3.141592653589793;
const float EWA_RADIUS = ${ewaRadius.toPrecision(12)};
const float EWA_FIRST = ${ewaRadiusZero.toPrecision(12)};
const float EWA_AR = 0.8;
bool useCleanSource;
bool useLegacySource;

vec3 fetchSource(ivec2 p) {
  ivec2 size = ivec2(u_sourceSize);
  p = (p % size + size) % size;
  if (useCleanSource) return texelFetch(u_cleanSource, p, 0).rgb;
  if (useLegacySource) return texelFetch(u_legacySource, p, 0).rgb;
  return texelFetch(u_source, p, 0).rgb;
}
vec3 bilinear(vec2 p) {
  ivec2 base = ivec2(floor(p));
  vec2 t = fract(p);
  return mix(
    mix(fetchSource(base), fetchSource(base + ivec2(1, 0)), t.x),
    mix(fetchSource(base + ivec2(0, 1)), fetchSource(base + ivec2(1, 1)), t.x),
    t.y);
}
vec3 area(vec2 target, vec2 scale) {
  vec2 first = target * scale + u_motion;
  vec2 last = first + scale;
  ivec2 base = ivec2(floor(first));
  vec3 sum = vec3(0.0);
  for (int y = 0; y < 3; y++) {
    float sy = float(base.y + y);
    float wy = max(0.0, min(last.y, sy + 1.0) - max(first.y, sy));
    for (int x = 0; x < 3; x++) {
      float sx = float(base.x + x);
      float wx = max(0.0, min(last.x, sx + 1.0) - max(first.x, sx));
      sum += fetchSource(base + ivec2(x, y)) * wx * wy;
    }
  }
  return sum / (scale.x * scale.y);
}
vec3 sbms(vec2 target, vec2 center, vec2 scale) {
  bool supported = scale.x > 1.0001 && scale.x <= 2.0 &&
                   scale.y > 1.0001 && scale.y <= 2.0;
  if (!supported) return bilinear(center);
  vec3 a = area(target, scale);
  vec3 low = (bilinear(center - vec2(0.5, 0.0)) + 2.0 * a +
              bilinear(center + vec2(0.5, 0.0))) * 0.25;
  const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
  float al = dot(a, luma), ll = dot(low, luma);
  vec3 ac = a - al, lc = low - ll;
  float saturation = max(low.r, max(low.g, low.b)) - min(low.r, min(low.g, low.b));
  float gate = clamp(length(ac - lc) * 8.0, 0.0, 1.0) *
               (1.0 - smoothstep(0.04, 0.18, saturation)) * 0.85;
  return clamp(al + mix(ac, lc, gate), 0.0, 1.0);
}
float radialWeight(float distance) {
  if (distance >= EWA_RADIUS) return 0.0;
  float p = distance / EWA_RADIUS * 255.0;
  int i = int(floor(p));
  return mix(texelFetch(u_ewaLut, ivec2(i, 0), 0).r,
             texelFetch(u_ewaLut, ivec2(min(i + 1, 255), 0), 0).r, fract(p));
}
vec3 power32(vec3 x) {
  x *= x; x *= x; x *= x; x *= x; x *= x;
  return x;
}
vec3 ewa(vec2 center) {
  ivec2 base = ivec2(floor(center));
  vec2 frac = fract(center);
  vec3 sum = vec3(0.0);
  float total = 0.0;
  vec3 dark = vec3(0.0), light = vec3(0.0);
  vec3 darkWeight = vec3(0.0), lightWeight = vec3(0.0);
  // Effective support after blur is below four source pixels.
  for (int y = -3; y <= 4; y++) {
    for (int x = -3; x <= 4; x++) {
      float d = length(vec2(float(x), float(y)) - frac);
      if (d >= EWA_RADIUS) continue;
      float w = radialWeight(d);
      vec3 c = fetchSource(base + ivec2(x, y));
      sum += w * c;
      total += w;
      if (d <= EWA_FIRST) {
        vec3 wd = w * power32(vec3(1.1) - c);
        vec3 wl = w * power32(c + 0.1);
        dark += wd * (1.0 - c);
        light += wl * c;
        darkWeight += wd;
        lightWeight += wl;
      }
    }
  }
  vec3 value = sum / total;
  vec3 lo = 1.0 - dark / darkWeight, hi = light / lightWeight;
  // Main-lobe weighted soft extrema, matching libplacebo's polar anti-ring.
  vec3 limited = min(max(value, lo), hi);
  limited = mix(limited, (lo + hi) * 0.5, greaterThan(lo, hi));
  return clamp(mix(value, limited, EWA_AR), 0.0, 1.0);
}
float lanczos(float x) {
  x = abs(x);
  if (x >= 3.0) return 0.0;
  if (x < 0.0000001) return 1.0;
  float p = PI * x;
  return (sin(p) / p) * (sin(p / 3.0) / (p / 3.0));
}
vec3 prefiltered(vec2 center, vec2 scale) {
  vec2 stretch = max(scale, vec2(1.0));
  vec2 radius = 3.0 * stretch;
  ivec2 first = ivec2(floor(center - radius)) + 1;
  ivec2 last = ivec2(floor(center + radius));
  float wx[20];
  float wy[20];
  for (int i = 0; i < 20; i++) {
    wx[i] = first.x + i <= last.x ? lanczos((float(first.x + i) - center.x) / stretch.x) : 0.0;
    wy[i] = first.y + i <= last.y ? lanczos((float(first.y + i) - center.y) / stretch.y) : 0.0;
  }
  vec3 sum = vec3(0.0);
  float total = 0.0;
  for (int y = 0; y < 20; y++) {
    if (first.y + y > last.y) break;
    if (abs(wy[y]) < 0.00000001) continue;
    for (int x = 0; x < 20; x++) {
      if (first.x + x > last.x) break;
      float w = wx[x] * wy[y];
      sum += fetchSource(first + ivec2(x, y)) * w;
      total += w;
    }
  }
  return clamp(sum / total, 0.0, 1.0);
}
void main() {
  vec2 target = vec2(floor(gl_FragCoord.x), u_outputSize.y - 1.0 - floor(gl_FragCoord.y));
  target = floor(u_viewOrigin + target / u_viewZoom);
  vec2 scale = u_sourceSize / u_outputSize;
  vec2 center = (target + 0.5) * scale - 0.5 + u_motion;
  bool leftSide = u_mode == 1 || (u_mode == 0 && gl_FragCoord.x / u_outputSize.x < u_split);
  bool native = all(lessThan(abs(scale - 1.0), vec2(0.000001)));
  useCleanSource = (!leftSide || u_comparePrevious != 0) && !(native && u_nativePassthrough != 0);
  useLegacySource = leftSide && u_compareLegacy != 0;
  vec3 c;
  if (leftSide && u_compareSbms != 0) c = sbms(target, center, scale);
  else if (native) c = bilinear(center);
  else if (any(greaterThan(scale, vec2(1.000001)))) c = prefiltered(center, scale);
  else {
    c = ewa(center);
    if (u_upscaleChroma != 0 && !(leftSide && u_comparePrevious != 0)) {
      // Keep EWA luminance detail. Reconstruct chroma with positive weights,
      // so the sharp kernel cannot amplify residual subpixel color fringes.
      const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
      vec3 base = bilinear(center);
      // Avoid rounding an already-achromatic result through luma subtraction.
      if (any(notEqual(c, c.ggg)) || any(notEqual(base, base.ggg))) {
        c = clamp(vec3(dot(c, luma)) + base - dot(base, luma), 0.0, 1.0);
      }
    }
  }
  outColor = vec4(c, 1.0);
}`;

  function strokeConfidenceAt(fetch) {
    const c = defringeConfig;
    const smooth = (lo, hi, value) => {
      const t = clip((value - lo) / (hi - lo));
      return t * t * (3 - 2 * t);
    };
    const luma = a => a[0] * 0.2126 + a[1] * 0.7152 + a[2] * 0.0722;
    const saturation = a => Math.max(...a) - Math.min(...a);
    const row = Array.from({length: 9}, (_, i) => fetch(i - 4, 0));
    const light = row.map(luma);
    const neutral = row.map(a => 1 - smooth(c.neutralLow, c.neutralHigh, saturation(a)));
    let confidence = 0;
    for (let l = 0; l < 4; l++) {
      for (let r = 5; r < 9; r++) {
        const anchors = neutral[l] * neutral[r] *
          (1 - smooth(c.backgroundLow, c.backgroundHigh, Math.abs(light[l] - light[r])));
        if (anchors <= 0) continue;
        const background = (light[l] + light[r]) / 2, dark = light[4] < background;
        const direction = dark ? 1 : -1;
        let extreme = dark ? 1 : 0, core = l + 1;
        for (let j = l; j <= r; j++) {
          extreme = dark ? Math.min(extreme, ...row[j]) : Math.max(extreme, ...row[j]);
          if (j > l && j < r && direction * light[j] < direction * light[core]) core = j;
        }
        let error = 1;
        for (const channels of [[0, 1, 2], [2, 1, 0]]) {
          let previous = row[l][channels[0]], variation = 0;
          for (let j = l; j <= r; j++) {
            for (const k of channels) {
              variation += Math.abs(row[j][k] - previous);
              previous = row[j][k];
            }
          }
          error = Math.min(error, Math.max(0, variation - Math.abs(row[l][channels[0]] - extreme) -
            Math.abs(row[r][channels[2]] - extreme)) / (variation + 0.00001));
        }
        const model = (1 - smooth(c.orderLow, c.orderHigh, error)) *
          smooth(c.contrastLow, c.contrastHigh, direction * (background - extreme));
        if (model <= 0) continue;
        let support = 0;
        for (const side of [-1, 1]) {
          for (let distance = 1; distance <= strokeReach; distance++) {
            let connected = false;
            for (let dx = -1; dx <= 1; dx++) {
              const neighbor = fetch(core - 4 + dx, side * distance), value = luma(neighbor);
              if (direction * (background - value) > c.contrastLow) connected = true;
              const evidence = (1 - smooth(c.neutralLow, c.neutralHigh, saturation(neighbor))) *
                (1 - smooth(c.rangeLow, c.rangeHigh, Math.abs(value - extreme)));
              support = Math.max(support, evidence);
            }
            if (!connected || support >= 1) break;
          }
        }
        confidence = Math.max(confidence, anchors * model * support);
      }
    }
    return confidence;
  }

  // Scalar reference for the source prepass. The caller quantizes to RGBA8
  // before sampleAt(..., 'hq'), exactly as the GPU framebuffer does.
  function defringeAt(data, width, height, x, y, refine = false) {
    const cfg = defringeConfig;
    const smooth = (lo, hi, x) => {
      const t = clip((x - lo) / (hi - lo));
      return t * t * (3 - 2 * t);
    };
    const dot = (a, b) => a.reduce((s, v, i) => s + v * b[i], 0);
    const luma = c => c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
    const fetch = (ix, iy = y) => {
      const p = (((iy % height + height) % height) * width + (ix % width + width) % width) * 4;
      return [data[p] / 255, data[p + 1] / 255, data[p + 2] / 255];
    };
    const original = fetch(x);
    if (Math.max(...original) === Math.min(...original)) return original;
    const colors = Array.from({length: 7}, (_, i) => fetch(x + i - 3));
    const light = colors.map(luma);
    const neutral = colors.map(c => 1 - smooth(cfg.neutralLow, cfg.neutralHigh, Math.max(...c) - Math.min(...c)));
    const chroma = original.map(c => c - light[3]), magnitude = Math.hypot(...chroma);
    let confidence = 0;
    for (let l = 0; l < 3; l++) {
      for (let r = 4; r < 7; r++) {
        const anchors = neutral[l] * neutral[r];
        if (anchors <= 0) continue;
        const lo = Math.min(light[l], light[r]), hi = Math.max(light[l], light[r]);
        const inRange = 1 - smooth(cfg.rangeLow, cfg.rangeHigh, Math.max(lo - light[3], light[3] - hi));
        const edge = smooth(cfg.contrastLow, cfg.contrastHigh, hi - lo) * inRange;
        const sameBackground = 1 - smooth(cfg.backgroundLow, cfg.backgroundHigh, hi - lo);
        const background = (light[l] + light[r]) * 0.5, excursion = light[3] - background;
        let pairEvidence = 0;
        if (sameBackground > 0) {
          for (let j = l + 1; j < r; j++) {
            if (j === 3) continue;
            const other = colors[j].map(c => c - light[j]), otherMagnitude = Math.hypot(...other);
            if (otherMagnitude < 1e-6 || excursion * (light[j] - background) <= 0) continue;
            const opposition = -dot(chroma, other) / (magnitude * otherMagnitude);
            const balance = Math.min(magnitude, otherMagnitude) / Math.max(magnitude, otherMagnitude);
            const stroke = smooth(cfg.contrastLow, cfg.contrastHigh, Math.min(Math.abs(excursion), Math.abs(light[j] - background)));
            pairEvidence = Math.max(pairEvidence, stroke *
              smooth(cfg.oppositeLow, cfg.oppositeHigh, opposition) *
              smooth(cfg.balanceLow, cfg.balanceHigh, balance));
          }
        }
        confidence = Math.max(confidence, anchors * Math.max(edge, sameBackground * pairEvidence));
      }
    }
    if (refine && (confidence > 0 || refine === 2)) {
      const saturation = c => Math.max(...c) - Math.min(...c);
      const direction = c => c.map(v => v - (c[0] + c[1] + c[2]) / 3);
      const ordered = c => Math.max(0, Math.min(c[0], c[2]) - c[1],
        c[1] - Math.max(c[0], c[2])) / (saturation(c) + 0.00001);
      const centerDirection = direction(original), centerMagnitude = Math.hypot(...centerDirection);
      let order = ordered(original), reversal = 0;
      for (let dy = -1; dy <= 1; dy++) {
        for (let dx = -1; dx <= 1; dx++) {
          if (!dx && !dy) continue;
          const neighbor = fetch(x + dx, y + dy), other = direction(neighbor);
          const otherMagnitude = Math.hypot(...other);
          if (otherMagnitude < centerMagnitude * cfg.relativeChroma ||
              dot(centerDirection, other) <= cfg.coherentCosine * centerMagnitude * otherMagnitude) continue;
          order = Math.max(order, ordered(neighbor) * smooth(cfg.neutralLow, cfg.neutralHigh, saturation(neighbor)));
          if (dy === 0) {
            const pair = dx < 0 ? [neighbor, original] : [original, neighbor];
            let best = Infinity;
            for (const channels of [[0, 1, 2], [2, 1, 0]]) {
              const sequence = pair.flatMap(c => channels.map(k => c[k]));
              let variation = 0;
              for (let i = 1; i < sequence.length; i++) variation += Math.abs(sequence[i] - sequence[i - 1]);
              best = Math.min(best, Math.max(0, variation - Math.abs(sequence[5] - sequence[0])) / (variation + 0.00001));
            }
            reversal = Math.max(reversal, best);
          }
        }
      }
      const gate = (1 - smooth(cfg.orderLow, cfg.orderHigh, order)) *
        (1 - smooth(cfg.orderLow, cfg.orderHigh, reversal));
      if (refine === 2 && gate > 0 && confidence < 1 && saturation(original) >= cfg.neutralHigh)
        confidence = Math.max(confidence, strokeConfidenceAt((dx, dy) => fetch(x + dx, y + dy)));
      confidence *= gate;
    }
    return original.map(c => mix(c, light[3], cfg.strength * confidence));
  }
  const refinedDefringeAt = (data, width, height, x, y) => defringeAt(data, width, height, x, y, true);
  const strokeDefringeAt = (data, width, height, x, y) => defringeAt(data, width, height, x, y, 2);

  // Independent scalar reference for sampled-pixel checks. Input is an RGBA
  // byte array; return value is normalized RGB (0..1). Coordinates are top-down.
  // 'hq' preserves the previous RGB EWA; 'hq-stable' uses positive-weight
  // chroma only for enlargement. Motion is in integer source pixels.
  function sampleAt(data, width, height, outWidth, outHeight, x, y,
                    motion = [0, 0], method = 'sbms') {
    const sx = width / outWidth, sy = height / outHeight;
    const mx = motion[0], my = motion[1];
    const cx = (x + 0.5) * sx - 0.5 + mx;
    const cy = (y + 0.5) * sy - 0.5 + my;
    function fetch(ix, iy) {
      ix = (ix % width + width) % width;
      iy = (iy % height + height) % height;
      const p = (iy * width + ix) * 4;
      return [data[p] / 255, data[p + 1] / 255, data[p + 2] / 255];
    }
    function linear(px, py) {
      const ix = Math.floor(px), iy = Math.floor(py), tx = px - ix, ty = py - iy;
      const a = fetch(ix, iy), b = fetch(ix + 1, iy),
        c = fetch(ix, iy + 1), d = fetch(ix + 1, iy + 1);
      return a.map((v, k) => mix(mix(v, b[k], tx), mix(c[k], d[k], tx), ty));
    }
    if (method === 'sbms' || method === 0 || method === 1) {
      if (!usesArea(sx, sy)) return linear(cx, cy);
      const fx = x * sx + mx, fy = y * sy + my, sum = [0, 0, 0];
      for (let iy = Math.floor(fy); iy < fy + sy; iy++) {
        const wy = Math.max(0, Math.min(fy + sy, iy + 1) - Math.max(fy, iy)) / sy;
        for (let ix = Math.floor(fx); ix < fx + sx; ix++) {
          const wx = Math.max(0, Math.min(fx + sx, ix + 1) - Math.max(fx, ix)) / sx;
          const c = fetch(ix, iy);
          for (let k = 0; k < 3; k++) sum[k] += c[k] * wx * wy;
        }
      }
      const left = linear(cx - 0.5, cy), right = linear(cx + 0.5, cy);
      const low = sum.map((v, k) => (left[k] + 2 * v + right[k]) / 4);
      const luma = v => v[0] * 0.2126 + v[1] * 0.7152 + v[2] * 0.0722;
      const al = luma(sum), ll = luma(low);
      const ac = sum.map(v => v - al), lc = low.map(v => v - ll);
      const delta = Math.hypot(...ac.map((v, k) => v - lc[k]));
      const saturation = Math.max(...low) - Math.min(...low);
      const t = clip((saturation - 0.04) / 0.14);
      const gate = clip(delta * 8) * (1 - t * t * (3 - 2 * t)) * 0.85;
      return ac.map((v, k) => clip(al + mix(v, lc[k], gate)));
    }
    if (Math.abs(sx - 1) < 1e-6 && Math.abs(sy - 1) < 1e-6) return linear(cx, cy);
    const sum = [0, 0, 0];
    let total = 0;
    if (sx > 1.000001 || sy > 1.000001) {
      const stretchX = Math.max(1, sx), stretchY = Math.max(1, sy);
      for (let iy = Math.floor(cy - 3 * stretchY) + 1; iy <= cy + 3 * stretchY; iy++) {
        const wy = lanczos3((iy - cy) / stretchY);
        for (let ix = Math.floor(cx - 3 * stretchX) + 1; ix <= cx + 3 * stretchX; ix++) {
          const w = wy * lanczos3((ix - cx) / stretchX), c = fetch(ix, iy);
          for (let k = 0; k < 3; k++) sum[k] += w * c[k];
          total += w;
        }
      }
      return sum.map(v => clip(v / total));
    }
    const dark = [0, 0, 0], light = [0, 0, 0];
    const darkW = [0, 0, 0], lightW = [0, 0, 0];
    for (let iy = Math.floor(cy) - 3; iy <= Math.floor(cy) + 4; iy++) {
      for (let ix = Math.floor(cx) - 3; ix <= Math.floor(cx) + 4; ix++) {
        const distance = Math.hypot(ix - cx, iy - cy), w = ewaWeight(distance);
        if (!w) continue;
        const c = fetch(ix, iy);
        total += w;
        for (let k = 0; k < 3; k++) {
          sum[k] += w * c[k];
          if (distance <= ewaRadiusZero) {
            const wd = w * (1.1 - c[k]) ** 32, wl = w * (0.1 + c[k]) ** 32;
            dark[k] += wd * (1 - c[k]); light[k] += wl * c[k];
            darkW[k] += wd; lightW[k] += wl;
          }
        }
      }
    }
    const detail = sum.map((v, k) => {
      v /= total;
      const lo = 1 - dark[k] / darkW[k], hi = light[k] / lightW[k];
      const limited = lo > hi ? (lo + hi) / 2 : Math.max(lo, Math.min(hi, v));
      return clip(mix(v, limited, ewaConfig.antiring));
    });
    if (method !== 'hq-stable') return detail;
    const luma = c => 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    const base = linear(cx, cy);
    if (detail[0] === detail[1] && detail[1] === detail[2] &&
        base[0] === base[1] && base[1] === base[2]) return detail;
    const detailY = luma(detail), baseY = luma(base);
    return base.map(v => clip(detailY + v - baseY));
  }
  window.DynamicFilter = Object.freeze({
    vertexSource, fragmentSource, defringeFragmentSource, defringeConfig,
    ewaConfig, ewaLut, ewaRadius, ewaRadiusZero,
    ewaWeight, jinc, lanczos3, profile, sampleAt, defringeAt, refinedDefringeAt, strokeDefringeAt,
    sourceRevision: '285d1cc8af2ab79b72ac662eb09736de8c12392b',
    maxDownscale: 3,
  });
})();
