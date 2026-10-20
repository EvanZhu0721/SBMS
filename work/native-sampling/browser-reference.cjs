// Independent GPU reference from the accepted experiment. Only test-boundary
// wrapping and the 20-tap demo cap are changed, never the production shaders.
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const {spawn} = require('node:child_process');
const root = __dirname;
const inputs = JSON.parse(fs.readFileSync(path.join(root, 'inputs.json'), 'utf8'));
const shader = fs.readFileSync(path.join(root, '../sampling-lab/dynamic-filter.js'), 'utf8');
const delay = ms => new Promise(r => setTimeout(r, ms));
const chrome = process.env.SBMS_REVIEW_CHROME ||
  'C:/Users/EZsTp/AppData/Local/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-win64/chrome-headless-shell.exe';

function installReference() {
  const F = window.DynamicFilter;
  const canvas = document.createElement('canvas');
  const gl = canvas.getContext('webgl2', {alpha: false, antialias: false, depth: false,
    stencil: false, preserveDrawingBuffer: true});
  if (!gl) throw Error('WebGL2 unavailable');
  gl.disable(gl.DITHER);
  gl.bindVertexArray(gl.createVertexArray());
  const substitutions = [
    ['(p + ivec2(x,y) + size) % size', 'clamp(p + ivec2(x,y), ivec2(0), size - 1)'],
    ['((p + ivec2(i - 4, 0)) % size + size) % size', 'clamp(p + ivec2(i - 4, 0), ivec2(0), size - 1)'],
    ['((p + ivec2(core - 4 + dx, (side == 0 ? -1 : 1) * distance)) % size + size) % size',
      'clamp(p + ivec2(core - 4 + dx, (side == 0 ? -1 : 1) * distance), ivec2(0), size - 1)'],
    ['ivec2((p.x + i - 3 + size.x) % size.x, p.y)',
      'clamp(p + ivec2(i - 3, 0), ivec2(0), size - 1)'],
  ];
  let cleanSource = F.defringeFragmentSource;
  for (const [before, after] of substitutions) {
    if (!cleanSource.includes(before)) throw Error('Missing clamp substitution: ' + before);
    cleanSource = cleanSource.replace(before, after);
  }
  if (!F.fragmentSource.includes('(p % size + size) % size')) throw Error('Missing sample boundary');
  const finalSource = F.fragmentSource
    .replace('(p % size + size) % size', 'clamp(p, ivec2(0), size - 1)')
    .replaceAll('[20]', '[64]').replaceAll('< 20;', '< 64;');
  function compile(type, source) {
    const s = gl.createShader(type); gl.shaderSource(s, source); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw Error(gl.getShaderInfoLog(s));
    return s;
  }
  function program(source) {
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, F.vertexSource));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, source)); gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw Error(gl.getProgramInfoLog(p));
    return p;
  }
  const cleanProgram = program(cleanSource), finalProgram = program(finalSource);
  const tex = (unit, format, width, height, type, data) => {
    const t = gl.createTexture(); gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, t);
    for (const axis of [gl.TEXTURE_WRAP_S, gl.TEXTURE_WRAP_T]) gl.texParameteri(gl.TEXTURE_2D, axis, gl.CLAMP_TO_EDGE);
    for (const mode of [gl.TEXTURE_MIN_FILTER, gl.TEXTURE_MAG_FILTER]) gl.texParameteri(gl.TEXTURE_2D, mode, gl.NEAREST);
    gl.texImage2D(gl.TEXTURE_2D, 0, format, width, height, 0, format === gl.R32F ? gl.RED : gl.RGBA, type, data);
    return t;
  };
  tex(1, gl.R32F, F.ewaLut.length, 1, gl.FLOAT, F.ewaLut);
  const fb = gl.createFramebuffer();
  const set = (p, name, type, ...v) => gl[type](gl.getUniformLocation(p, name), ...v);
  const base64 = bytes => {
    let s = '';
    for (let i = 0; i < bytes.length; i += 32768) s += String.fromCharCode(...bytes.subarray(i, i + 32768));
    return btoa(s);
  };
  window.nativeReference = request => {
    const {sw, sh, tw, th, input} = request;
    canvas.width = tw; canvas.height = th;
    const data = Uint8Array.from(atob(input), c => c.charCodeAt(0));
    const source = tex(0, gl.RGBA8, sw, sh, gl.UNSIGNED_BYTE, data);
    const clean = tex(2, gl.RGBA8, sw, sh, gl.UNSIGNED_BYTE, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, clean, 0);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw Error('Incomplete clean target');
    gl.useProgram(cleanProgram); gl.viewport(0, 0, sw, sh);
    set(cleanProgram, 'u_source', 'uniform1i', 0);
    set(cleanProgram, 'u_sourceSize', 'uniform2f', sw, sh);
    set(cleanProgram, 'u_refine', 'uniform1i', 2);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    const cleaned = new Uint8Array(sw * sh * 4);
    gl.readPixels(0, 0, sw, sh, gl.RGBA, gl.UNSIGNED_BYTE, cleaned);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, tw, th);
    gl.useProgram(finalProgram);
    for (const [n, v] of Object.entries({u_source: 0, u_cleanSource: 2, u_legacySource: 0,
      u_ewaLut: 1, u_nativePassthrough: 0, u_upscaleChroma: 1,
      u_compareLegacy: 0, u_comparePrevious: 0})) set(finalProgram, n, 'uniform1i', v);
    set(finalProgram, 'u_sourceSize', 'uniform2f', sw, sh);
    set(finalProgram, 'u_outputSize', 'uniform2f', tw, th);
    set(finalProgram, 'u_motion', 'uniform2f', 0, 0);
    set(finalProgram, 'u_viewOrigin', 'uniform2f', 0, 0);
    set(finalProgram, 'u_viewZoom', 'uniform1f', 1);
    const result = {cleaned: base64(cleaned)};
    for (const [mode, legacy] of [['quality', false], ['legacy', true]]) {
      set(finalProgram, 'u_mode', 'uniform1i', legacy ? 1 : 2);
      set(finalProgram, 'u_compareSbms', 'uniform1i', legacy ? 1 : 0);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      const bytes = new Uint8Array(tw * th * 4), upright = new Uint8Array(bytes.length);
      gl.readPixels(0, 0, tw, th, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
      for (let y = 0; y < th; y++) upright.set(bytes.subarray((th - 1 - y) * tw * 4, (th - y) * tw * 4), y * tw * 4);
      result[mode] = base64(upright);
    }
    gl.deleteTexture(source); gl.deleteTexture(clean);
    const err = gl.getError(); if (err) throw Error('GL error: ' + err);
    return result;
  };
  const debug = gl.getExtension('WEBGL_debug_renderer_info');
  return {renderer: gl.getParameter(debug ? debug.UNMASKED_RENDERER_WEBGL : gl.RENDERER),
    changes: ['repeat→clamp for display boundaries', 'Lanczos cap 20→64 for ratios over 3'],
    acceptedExperiment: 'work/sampling-lab/dynamic-filter.js', cleanFormat: 'RGBA8'};
}
(async () => {
  const server = net.createServer();
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const port = server.address().port; await new Promise(r => server.close(r));
  const proc = spawn(chrome, [`--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1',
    '--use-angle=d3d11', '--no-first-run', 'about:blank'], {windowsHide: true, stdio: 'ignore'});
  let ws;
  try {
    let pages;
    for (let n = 0; n < 100; n++) {
      try { pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); if (pages.length) break; } catch {}
      await delay(100);
    }
    if (!pages?.length) throw Error('Chrome did not start');
    ws = new WebSocket(pages[0].webSocketDebuggerUrl);
    await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
    let serial = 0; const pending = new Map();
    ws.onmessage = e => {
      const m = JSON.parse(e.data), p = pending.get(m.id); if (!p) return;
      pending.delete(m.id); m.error ? p.reject(m.error) : p.resolve(m.result);
    };
    const call = (method, params) => new Promise((resolve, reject) => {
      const id = ++serial; pending.set(id, {resolve, reject}); ws.send(JSON.stringify({id, method, params}));
    });
    const evaluate = async expression => {
      const r = await call('Runtime.evaluate', {expression, awaitPromise: true, returnByValue: true});
      if (r.exceptionDetails) throw Error(JSON.stringify(r.exceptionDetails)); return r.result.value;
    };
    await evaluate(shader);
    const report = await evaluate(`(${installReference.toString()})()`);
    if (/swiftshader|llvmpipe/i.test(report.renderer)) throw Error('Hardware GPU required');
    report.cases = [];
    for (const c of inputs.cases) {
      const input = fs.readFileSync(path.join(root, c.input)).toString('base64');
      const output = await evaluate(`nativeReference(${JSON.stringify({...c, input})})`);
      for (const name of ['quality', 'legacy', 'cleaned'])
        fs.writeFileSync(path.join(root, `${c.id}.reference-${name}.rgba`), Buffer.from(output[name], 'base64'));
      report.cases.push(c.id); console.log(c.id);
    }
    fs.writeFileSync(path.join(root, 'browser-reference.json'), JSON.stringify(report, null, 2));
  } finally { ws?.close(); proc.kill(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
