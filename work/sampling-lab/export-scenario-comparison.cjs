// Export release/new GPU paths by content, at one common reduction per set.
const fs = require('node:fs'), path = require('node:path'), net = require('node:net');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process'), {pathToFileURL} = require('node:url');
const stress = process.argv.includes('--stress');
const scenario = stress ? '6k2k' : 'down50', sourceDpi = stress ? 230.4 : 192;
const root = __dirname, out = path.join(root, stress ? 'stress-exports' : 'scenario-exports');
const release = JSON.parse(fs.readFileSync(path.join(root, 'release-baseline.json'), 'utf8'));
assert(release.verification.baselineHlslMatchesReleaseShader);
assert(release.verification.workingTreeShaderMatchesRelease);
const report = {
  release, sourceSize: stress ? [6144, 3456] : [3840, 2160],
  outputSize: stress ? [2560, 1440] : [1920, 1080],
  scope: 'Released sampling formula reproduced in WebGL versus the experimental final filter; not installed-app screenshots or a performance/streaming test.',
  conditions: `Every scene uses ${scenario}, with ${sourceDpi / 96 * 100}% text/layout DPI where applicable. Both sides share source bytes and source offset.`,
  phases: [], failures: [], errors: []
};
const manifest = {
  release, sourceSize: report.sourceSize, outputSize: report.outputSize,
  atlas: 'pixels.png', sourceDpi, outputDpi: 96, sourceMotion: [0, 0],
  scope: report.scope, tiles: []
};
const save = () => {
  fs.writeFileSync(path.join(out, 'verification.json'), JSON.stringify(report, null, 2));
  fs.writeFileSync(path.join(out, 'manifest.json'), JSON.stringify(manifest, null, 2));
};
const cases = [];
for (const kind of ['cleartype', 'grayscale']) {
  for (const scene of ['reading', 'code']) cases.push({
    id: `${scene}-${kind}`, scene,
    label: kind === 'cleartype' ? 'ClearType' : 'Grayscale antialiasing',
    state: {content: `windows-gdi-adaptive-${kind}`}, type: 'capture',
    relative: scene === 'reading'
      ? {x: 24, y: 14, width: 1072, height: 136}
      : {x: 24, y: 254, width: 1072, height: 216},
    detail: scene === 'reading'
      ? {x: 0, y: 40, width: 64, height: 28}
      : {x: 82, y: 80, width: 64, height: 28},
    provenance: 'Native Win32 GDI test-window capture, 192 target DPI, 24 px source em -> 12 px output em. Not a screenshot of a named office/editor application.'
  });
}
cases.push({
  id: 'lines', scene: 'lines', label: '0.5 / 1 / 2 DIP', type: 'synthetic',
  state: {content: 'positive'},
  absolute: {x: 40, y: 1160, width: 1200, height: 920},
  detail: {x: 55, y: 248, width: 80, height: 48},
  provenance: 'Existing canvas-generated line/circle/Bezier fixture, independently drawn at 200% layout density. No source-pixel stripes in this crop.'
});
for (const size of [96, 32]) for (const asset of ['tiktok', 'vlc', 'firefox', 'vscode', 'git', 'rainbow-flag']) {
  cases.push({
    id: `${asset}-${size}`, scene: 'icons', label: `${asset} · ${size / 2} px`,
    type: 'icon', state: {content: asset, assetSize: size, background: '#ffffff'},
    detail: size === 96 && ['tiktok', 'vlc'].includes(asset)
      ? {x: 0, y: 0, width: asset === 'vlc' ? 43 : 48, height: 48} : null,
    provenance: 'Published icon, rasterized once at the stated source size before both sampling paths. Source links: assets/corpus/SOURCES.md and the embedded TikTok source metadata.'
  });
}
for (const [asset, size, detail] of [
  ['astronaut', 512, {x: 80, y: 36, width: 64, height: 48}],
  ['coffee', 600, {x: 118, y: 44, width: 64, height: 48}]
]) cases.push({
  id: asset, scene: 'photos', label: asset === 'astronaut' ? 'Portrait / fabric' : 'Ceramic / texture',
  type: 'photo', state: {content: asset, assetSize: size, background: '#ffffff'},
  detail, provenance: 'Original PNG dimensions preserved in the source texture; only the tested final 2:1 reduction is applied. Source links: assets/corpus/SOURCES.md.'
});

if (stress) {
  cases.length = 0;
  for (const kind of ['cleartype', 'grayscale']) for (const scene of ['reading', 'code']) {
    cases.push({
      id: `${scene}-${kind}`, scene,
      label: kind === 'cleartype' ? 'ClearType' : 'Grayscale antialiasing',
      state: {content: `windows-gdi-adaptive-${kind}`}, type: 'capture',
      relative: scene === 'reading'
        ? {x: 29, y: 17, width: 1286, height: 163}
        : {x: 29, y: 305, width: 1286, height: 259},
      detail: scene === 'reading'
        ? {x: 0, y: 40, width: 64, height: 28}
        : {x: 96, y: 80, width: 68, height: 28},
      provenance: 'Independent native Win32 GDI capture at 230.4 target DPI (240%). GDI rounds the source em to 29 px, producing 12.0833 output px. Not a resized 100% screenshot or a named application screenshot.'
    });
  }
  cases.push({
    id: 'lines', scene: 'lines', label: '0.5 / 1 / 2 DIP sloping lines', type: 'synthetic',
    state: {content: 'positive'},
    absolute: {x: 48, y: 1812, width: 1440, height: 288},
    detail: {x: 10, y: 24, width: 100, height: 44},
    provenance: 'Existing synthetic canvas sloping lines independently drawn at 240% layout density. The crop shows all three line widths.'
  });
  for (const [id, label, y] of [
    ['stripes', '1-source-pixel stripes', 1908],
    ['checker', '2-source-pixel checks', 2436],
    ['diagonal', '1-source-pixel diagonal hatching', 2952]
  ]) cases.push({
    id, scene: 'texture', label, type: 'synthetic', state: {content: 'positive'},
    absolute: {x: 2112, y, width: 1440, height: 240},
    detail: {x: 32, y: 20, width: 64, height: 40},
    provenance: 'Existing synthetic fixed-source-pixel texture. Intentionally contains frequencies beyond the output Nyquist limit, unlike DPI-scaled UI text. Expected benefit is suppression of false patterns, not recovery of individual stripes.'
  });
  for (const asset of ['tiktok', 'vlc', 'flag-us', 'git']) cases.push({
    id: `${asset}-96`, scene: 'icons', label: `${asset} · ~40 px`,
    type: 'icon', state: {content: asset, assetSize: 96, background: '#ffffff'},
    detail: ['tiktok', 'vlc', 'flag-us'].includes(asset)
      ? {x: 0, y: 0, width: asset === 'vlc' ? 36 : 41, height: 41} : null,
    provenance: 'Published icon, rasterized at 96 source px before both sampling paths. Approximately 40 output px, including original intentional colors. Source links: assets/corpus/SOURCES.md and the embedded TikTok source metadata.'
  });
  manifest.sceneTitles = {
    reading: 'Small text · Stress test', code: 'Dark text & code · Stress test',
    lines: 'Fine lines · Stress test', texture: 'Dense textures · Stress test',
    icons: 'Real colors · Control'
  };
}

function installProbe() {
  window.sceneExports = [];
  const chroma = (a, p) => Math.max(a[p], a[p + 1], a[p + 2]) - Math.min(a[p], a[p + 1], a[p + 2]);
  const luma = (a, p) => .2126 * a[p] + .7152 * a[p + 1] + .0722 * a[p + 2];
  window.prepareScene = spec => {
    const l = dynamicLab, d = l.dimensions;
    let src = spec.absolute;
    if (!src) {
      const tile = l.corpus.regions.reduce((a, b) =>
        Math.hypot(a.x + a.width / 2 - d.sourceWidth / 2, a.y + a.height / 2 - d.sourceHeight / 2) <
        Math.hypot(b.x + b.width / 2 - d.sourceWidth / 2, b.y + b.height / 2 - d.sourceHeight / 2) ? a : b);
      src = spec.relative ? {...spec.relative, x: tile.x + spec.relative.x, y: tile.y + spec.relative.y} : {...tile};
    }
    const raw = l.source.getContext('2d').getImageData(0, 0, d.sourceWidth, d.sourceHeight).data;
    window.sceneCase = {spec, src, source: l.source, raw, clean: l.cleanPixels()};
    return {sourceRect: src, asset: l.corpus.activeAsset?.id || 'positive', dimensions: {...d}};
  };
  window.checkOriginalPhoto = async () => {
    const l = dynamicLab, a = sceneCase, img = new Image();
    img.src = l.corpus.activeAsset.dataUrl; await img.decode();
    const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
    const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
    const original = ctx.getImageData(0, 0, c.width, c.height).data; let differences = 0;
    if (c.width !== a.src.width || c.height !== a.src.height) throw Error('Photo was resized before sampling');
    for (let y = 0; y < c.height; y++) for (let x = 0; x < c.width; x++)
      for (let k = 0; k < 4; k++) differences += original[(y * c.width + x) * 4 + k] !==
        a.raw[((a.src.y + y) * l.dimensions.sourceWidth + a.src.x + x) * 4 + k];
    return {width: c.width, height: c.height, differences};
  };
  window.inspectScene = exportPixels => {
    const l = dynamicLab, d = l.dimensions, a = sceneCase;
    if (l.source !== a.source) throw Error('Source changed with phase');
    const m = l.motion, scale = d.width / d.sourceWidth;
    const x = Math.floor((a.src.x - m[0]) * scale), y = Math.floor((a.src.y - m[1]) * scale);
    const width = Math.ceil((a.src.x + a.src.width - m[0]) * scale) - x;
    const height = Math.ceil((a.src.y + a.src.height - m[1]) * scale) - y;
    if (x < 12 || y < 12 || x + width >= d.width - 12 || y + height >= d.height - 12) throw Error('ROI too close to texture boundary');
    const outputs = [l.pixels(1), l.pixels(2)], wipe = l.pixels(0);
    const stats = outputs.map(() => ({
      pixels: width * height, chromaSum: 0, chromaAtLeast8: 0, maxChroma: 0,
      lumaSum: 0, lumaSquaredSum: 0, minLuma: 255, maxLuma: 0
    }));
    const canvases = exportPixels ? outputs.map(() => {
      const c = document.createElement('canvas'); c.width = width; c.height = height; return c;
    }) : [];
    const images = canvases.map(c => c.getContext('2d').createImageData(width, height));
    let splitErrors = 0, rgbDelta = 0, lumaDelta = 0, maxLumaDelta = 0;
    const edges = [];
    for (let yy = 0; yy < height; yy++) for (let xx = 0; xx < width; xx++) {
      const p = ((d.height - 1 - y - yy) * d.width + x + xx) * 4;
      for (let side = 0; side < 2; side++) {
        const c = chroma(outputs[side], p), s = stats[side];
        s.chromaSum += c; s.chromaAtLeast8 += c >= 8; s.maxChroma = Math.max(s.maxChroma, c);
        const light = luma(outputs[side], p);
        s.lumaSum += light; s.lumaSquaredSum += light * light;
        s.minLuma = Math.min(s.minLuma, light); s.maxLuma = Math.max(s.maxLuma, light);
        if (exportPixels) images[side].data.set(outputs[side].subarray(p, p + 4), (yy * width + xx) * 4);
      }
      const dy = Math.abs(luma(outputs[0], p) - luma(outputs[1], p));
      lumaDelta += dy; maxLumaDelta = Math.max(maxLumaDelta, dy);
      for (let k = 0; k < 3; k++) rgbDelta += Math.abs(outputs[0][p + k] - outputs[1][p + k]);
      const expected = x + xx + .5 < d.width * l.state.split ? outputs[0] : outputs[1];
      for (let k = 0; k < 4; k++) splitErrors += wipe[p + k] !== expected[p + k];
      if (xx && Math.abs(luma(outputs[0], p) - luma(outputs[0], p - 4)) > 12) edges.push([x + xx, y + yy]);
    }
    const points = [];
    for (let yy = 0; yy < 6; yy++) for (let xx = 0; xx < 6; xx++)
      points.push([x + Math.floor((xx + .5) * width / 6), y + Math.floor((yy + .5) * height / 6)]);
    for (let i = 0; i < 48 && edges.length; i++) points.push(edges[Math.floor(i * edges.length / 48)]);
    const cpu = {samples: 0, sbmsMax: 0, finalMax: 0, mismatches: []};
    for (const [px, py] of points) for (let side = 0; side < 2; side++) {
      const exp = l.filter.sampleAt(side ? a.clean : a.raw, d.sourceWidth, d.sourceHeight,
        d.width, d.height, px, py, m, side ? 'hq-stable' : 'sbms').map(v => Math.round(v * 255));
      const p = ((d.height - 1 - py) * d.width + px) * 4;
      const err = Math.max(...exp.map((v, k) => Math.abs(v - outputs[side][p + k])));
      cpu.samples++; const key = side ? 'finalMax' : 'sbmsMax'; cpu[key] = Math.max(cpu[key], err);
      if (err > 1 && cpu.mismatches.length < 10) cpu.mismatches.push({px, py, side, err});
    }
    if (exportPixels) {
      canvases.forEach((c, i) => c.getContext('2d').putImageData(images[i], 0, 0));
      sceneExports.push({id: a.spec.id, canvases});
    }
    for (const s of stats) {
      s.lumaMean = s.lumaSum / s.pixels;
      s.lumaStdDev = Math.sqrt(Math.max(0, s.lumaSquaredSum / s.pixels - s.lumaMean ** 2));
      delete s.lumaSum; delete s.lumaSquaredSum;
    }
    return {
      id: a.spec.id, scene: a.spec.scene, motion: m, sourceRect: a.src,
      outputRect: {x, y, width, height}, metrics: {sbms: stats[0], final: stats[1]},
      rgbMeanAbsoluteDelta: rgbDelta / (width * height * 3),
      lumaMeanAbsoluteDelta: lumaDelta / (width * height), maxLumaDelta,
      splitErrors, cpu
    };
  };
  window.exportSceneAtlas = () => {
    const width = Math.max(...sceneExports.map(e => e.canvases[0].width)), gap = 16;
    const height = sceneExports.reduce((sum, e) => sum + e.canvases[0].height + gap, 0) - gap;
    const c = document.createElement('canvas'); c.width = width * 2 + gap; c.height = height;
    const ctx = c.getContext('2d'), positions = []; let y = 0;
    for (const entry of sceneExports) {
      const result = {id: entry.id};
      entry.canvases.forEach((source, side) => {
        const x = side * (width + gap); ctx.drawImage(source, x, y);
        const roundtrip = ctx.getImageData(x, y, source.width, source.height).data;
        const original = source.getContext('2d').getImageData(0, 0, source.width, source.height).data;
        for (let p = 0; p < original.length; p++) if (original[p] !== roundtrip[p]) throw Error('Atlas changed output pixels');
        result[side ? 'final' : 'sbms'] = {x, y, width: source.width, height: source.height};
      });
      positions.push(result); y += entry.canvases[0].height + gap;
    }
    return {width: c.width, height, positions, url: c.toDataURL()};
  };
}

(async () => {
  fs.mkdirSync(out, {recursive: true});
  const server = net.createServer(); await new Promise(r => server.listen(0, '127.0.0.1', r));
  const port = server.address().port; await new Promise(r => server.close(r));
  const chrome = 'C:/Users/EZsTp/AppData/Local/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-win64/chrome-headless-shell.exe';
  const proc = spawn(chrome, [`--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1',
    '--use-angle=d3d11', '--no-first-run', 'about:blank'], {windowsHide: true, stdio: ['ignore', 'ignore', 'pipe']});
  const delay = ms => new Promise(r => setTimeout(r, ms)); let ws, stderr = '';
  proc.stderr.on('data', d => stderr += d);
  try {
    let pages;
    for (let n = 0; n < 150; n++) {
      try {pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); if (pages.length) break;} catch {}
      await delay(100);
    }
    assert(pages?.length, stderr);
    ws = new WebSocket(pages[0].webSocketDebuggerUrl); await new Promise((r, j) => {ws.onopen = r; ws.onerror = j;});
    let id = 0; const pending = new Map(), exceptions = [];
    ws.onmessage = e => {
      const m = JSON.parse(e.data);
      if (m.id) {const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(m.error) : p.resolve(m.result);}
      else if (m.method === 'Runtime.exceptionThrown') exceptions.push(m.params);
    };
    const send = (method, params = {}) => new Promise((resolve, reject) => {
      const key = ++id; pending.set(key, {resolve, reject}); ws.send(JSON.stringify({id: key, method, params}));
    });
    const ev = async expression => {
      const r = await send('Runtime.evaluate', {expression, awaitPromise: true, returnByValue: true});
      if (r.exceptionDetails) throw Error(JSON.stringify(r.exceptionDetails)); return r.result.value;
    };
    await send('Page.enable'); await send('Runtime.enable');
    await send('Page.navigate', {url: pathToFileURL(path.join(root, 'dynamic.html')).href});
    for (let n = 0; n < 400; n++) {if (await ev('document.body?.dataset.ready === "true"')) break; await delay(100);}
    assert(await ev('document.body.dataset.ready === "true"'));
    await ev(`(${installProbe.toString()})()`);
    report.renderer = await ev('(()=>{const g=dynamicLab.gl,e=g.getExtension("WEBGL_debug_renderer_info");return e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER);})()');
    assert(!/swiftshader|llvmpipe/i.test(report.renderer));
    for (const spec of cases) {
      if (spec.state.assetSize) await ev(`(()=>{
        const asset=dynamicLab.corpus.assets.find(a=>a.id===${JSON.stringify(spec.state.content)});
        const size=${spec.state.assetSize};if(!asset.sizes.includes(size))asset.sizes.push(size);
      })()`);
      await ev(`dynamicLab.setState(${JSON.stringify({scenario, reference: 'sbms', playing: false,
        focus: false, time: 0, dpi: 'auto', motion: 'x', speed: 72, ...spec.state})})`);
      const prep = await ev(`prepareScene(${JSON.stringify(spec)})`);
      assert.deepEqual([prep.dimensions.sourceWidth, prep.dimensions.sourceHeight], report.sourceSize);
      assert(Math.abs(prep.dimensions.dpi - sourceDpi / 96 * 100) < 1e-6);
      assert.equal(prep.dimensions.area, !stress);
      assert.equal(prep.dimensions.up, false);
      if (spec.state.assetSize) assert.equal(Math.max(prep.sourceRect.width, prep.sourceRect.height), spec.state.assetSize);
      if (spec.type === 'photo') {
        prep.originalPhoto = await ev('checkOriginalPhoto()');
        assert.equal(prep.originalPhoto.differences, 0);
      }
      let first;
      for (const motion of [[0, 0], [1, 0], [1, 1]]) {
        await ev(`dynamicLab.setState(${JSON.stringify({motion: motion[1] ? 'xy' : 'x',
          time: motion[0] ? report.outputSize[0] / report.sourceSize[0] / 72 + 1e-8 : 0})})`);
        assert.deepEqual(await ev('dynamicLab.motion'), motion);
        const result = await ev(`inspectScene(${motion[0] === 0})`);
        report.phases.push(result); if (!motion[0]) first = result;
        if (result.cpu.sbmsMax > 1 || result.cpu.finalMax > 1 || result.splitErrors)
          report.failures.push({id: spec.id, motion, cpu: result.cpu, splitErrors: result.splitErrors});
        if (spec.id.endsWith('grayscale') && (result.metrics.sbms.maxChroma || result.metrics.final.maxChroma))
          report.failures.push({id: spec.id, motion, error: 'Grayscale gained chroma'});
      }
      const {state, relative, absolute, ...description} = spec;
      const {width, height} = first.outputRect;
      if (spec.detail) assert(spec.detail.x + spec.detail.width <= width && spec.detail.y + spec.detail.height <= height);
      manifest.tiles.push({...description, width, height, source: prep, outputRect: first.outputRect});
      save(); console.log(`${spec.id}: ${width}x${height}`);
    }
    const atlas = await ev('exportSceneAtlas()');
    fs.writeFileSync(path.join(out, 'pixels.png'), Buffer.from(atlas.url.split(',')[1], 'base64'));
    for (const tile of manifest.tiles) Object.assign(tile, atlas.positions.find(p => p.id === tile.id));
    manifest.atlasSize = [atlas.width, atlas.height];
    report.cpuSamples = report.phases.reduce((n, p) => n + p.cpu.samples, 0);
    report.browserExceptions = exceptions; report.dynamicErrors = await ev('dynamicLab.errors');
    if (exceptions.length || report.dynamicErrors.length) report.failures.push('Browser error');
    report.notes = [
      'Zero offset is exported; all three fixed source offsets are verified, without moving or rerasterizing the source.',
      'CPU checks use the actual GPU-preprocessed source, isolating resampling from previously audited defringe behavior.',
      'Color differences on icons and photos are not improvements. Neutral-text chroma is one diagnostic, not a total image quality score.',
      'The atlas copies GPU RGBA8 pixels directly. Figure construction must preserve 1:1 pixels and use nearest-neighbor only for labeled details.',
      `${sourceDpi} DPI describes native text target rasterization and canvas layout. Photo input dimensions remain original; icon source sizes are explicitly stated.`,
      '96 px icons and original-size photos are enabled only in this test tab, without changing the shipped demo size menu.'
    ];
    save();
    console.log(JSON.stringify({tiles: manifest.tiles.length, phases: report.phases.length, cpuSamples: report.cpuSamples, failures: report.failures}));
    if (report.failures.length) process.exitCode = 1;
    await send('Browser.close').catch(() => {});
  } catch (error) {
    report.errors.push(error.stack || String(error)); save(); console.error(error); process.exitCode = 1;
  } finally {
    if (ws?.readyState === WebSocket.OPEN) ws.close();
    if (proc.exitCode === null) proc.kill();
  }
})();
