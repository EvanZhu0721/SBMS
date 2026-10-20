// Bounded native-GDI evaluation of the SBMS mathematical reproduction vs final.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{pathToFileURL}=require('node:url');
const root=__dirname,outRoot=path.join(root,'final-exports'),delay=ms=>new Promise(r=>setTimeout(r,ms));
const chrome='C:/Users/EZsTp/AppData/Local/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-win64/chrome-headless-shell.exe';
const report={scope:'Actual browser GPU output with native GDI fonts: SBMS mathematical reproduction versus final; not production SBMS execution, codec evaluation or FPS benchmark.',cases:[],summary:[],screenshots:[],failures:[],errors:[]};
const exportsManifest={scope:report.scope,layout:'One unscaled RGBA8 atlas, top-down: eight CT scenarios by two sides. Six complete rows per panel with 32-pixel slots; 8-pixel gaps. Source offset (0,0) only; geometric row phases are recorded separately. Grayscale and all three source offsets are quantified in the main report.',cases:[],screenshots:[]};
const save=()=>{fs.writeFileSync(path.join(root,'sbms-final-verification.json'),JSON.stringify(report,null,2));fs.writeFileSync(path.join(outRoot,'manifest.json'),JSON.stringify(exportsManifest,null,2));};
function installProbe(){
  window.finalAtlas=[];
  const spread=(a,p)=>Math.max(a[p],a[p+1],a[p+2])-Math.min(a[p],a[p+1],a[p+2]);
  const luma=(a,p)=>.2126*a[p]+.7152*a[p+1]+.0722*a[p+2];
  const empty=()=>({pixels:0,chromaSum:0,chromaAtLeast8:0,maxChroma:0,alphaMin:255,alphaMax:0,horizontalLumaGradientSum:0,horizontalPairs:0,lumaOutsideSourceByOver1:0,maxLumaOvershoot:0,backgroundContrastAbsSum:0});
  window.prepareFinalCase=()=>{
    const l=dynamicLab,d=l.dimensions,a=l.corpus.activeAsset;
    const tile=l.corpus.regions.reduce((best,r)=>Math.hypot(r.x+r.width/2-d.sourceWidth/2,r.y+r.height/2-d.sourceHeight/2)<Math.hypot(best.x+best.width/2-d.sourceWidth/2,best.y+best.height/2-d.sourceHeight/2)?r:best);
    window.finalCase={source:l.source,raw:l.source.getContext('2d').getImageData(0,0,d.sourceWidth,d.sourceHeight).data,clean:l.cleanPixels(),tile,asset:a};
    return {asset:a.id,dpi:a.dpi,dimensions:{...d},tile,timerQueryAvailable:!!l.gl.getExtension('EXT_disjoint_timer_query_webgl2')};
  };
  window.inspectFinalPhase=(label,exportImages=false)=>{
    const l=dynamicLab,d=l.dimensions,a=finalCase.asset,tile=finalCase.tile,w=d.width,h=d.height,sx=w/d.sourceWidth,sy=h/d.sourceHeight,m=l.motion;
    if(l.source!==finalCase.source)throw Error('Source rebuilt while changing only phase');
    const outputs=[l.pixels(1),l.pixels(2)],totals=[empty(),empty()],rows=[],samples=[],cpu={sbmsMax:0,finalMax:0,samples:0,mismatches:[]};
    // The final native-pixel bypass contract is fixed by the main integration.
    const nativeBypass=d.native,inputs=[finalCase.raw,nativeBypass?finalCase.raw:finalCase.clean];
    const width=Math.max(...a.regions.map(r=>Math.ceil((tile.x+r.x+r.width-m[0])*sx)-Math.floor((tile.x+r.x-m[0])*sx))),slot=32,height=slot*6;
    const canvases=exportImages?[0,1].map(()=>{const c=document.createElement('canvas');c.width=width;c.height=height;return c;}):[];
    const contexts=canvases.map(c=>c.getContext('2d'));
    let changedPixels=0,lumaDifferenceSum=0,wipeErrors=0;
    for(let n=0;n<a.regions.length;n++){
      const r=a.regions[n],fx=(tile.x+r.x-m[0])*sx,fy=(tile.y+r.y-m[1])*sy;
      const x=Math.floor(fx),y=Math.floor(fy),rw=Math.ceil((tile.x+r.x+r.width-m[0])*sx)-x,rh=Math.ceil((tile.y+r.y+r.height-m[1])*sy)-y;
      if(rh>slot||x<0||y<0||x+rw>w||y+rh>h)throw Error('Incomplete output row');
      const row={id:r.id,text:r.text,font:r.actualFont||r.font,fontHeightPx:r.fontHeightPx,outputEm:Math.abs(r.fontHeightPx)*sy,source:{x:tile.x+r.x,y:tile.y+r.y,width:r.width,height:r.height},output:{x,y,width:rw,height:rh},phase:[fx-x,fy-y],export:{x:0,y:n*slot,width:rw,height:rh},metrics:[empty(),empty()]};
      let sourceLumaMin=255,sourceLumaMax=0;
      for(let yy=0;yy<r.height;yy++)for(let xx=0;xx<r.width;xx++){
        const value=luma(finalCase.raw,((tile.y+r.y+yy)*d.sourceWidth+tile.x+r.x+xx)*4);
        sourceLumaMin=Math.min(sourceLumaMin,value);sourceLumaMax=Math.max(sourceLumaMax,value);
      }
      const background=r.background==='#ffffff'?255:24;
      row.sourceLumaRange=[sourceLumaMin,sourceLumaMax];row.backgroundLuma=background;
      const glyphSamples=[];
      for(let side=0;side<2;side++){
        const data=outputs[side],stats=row.metrics[side],im=exportImages?contexts[side].createImageData(rw,rh):null;
        if(exportImages){contexts[side].fillStyle=r.background;contexts[side].fillRect(0,n*slot,width,slot);}
        for(let yy=0;yy<rh;yy++)for(let xx=0;xx<rw;xx++){
          const p=((h-1-y-yy)*w+x+xx)*4,c=spread(data,p);
          stats.pixels++;stats.chromaSum+=c;stats.chromaAtLeast8+=c>=8;stats.maxChroma=Math.max(stats.maxChroma,c);
          stats.alphaMin=Math.min(stats.alphaMin,data[p+3]);stats.alphaMax=Math.max(stats.alphaMax,data[p+3]);
          const value=luma(data,p),overshoot=Math.max(0,sourceLumaMin-value,value-sourceLumaMax);
          stats.lumaOutsideSourceByOver1+=overshoot>1+1e-9;stats.maxLumaOvershoot=Math.max(stats.maxLumaOvershoot,overshoot);stats.backgroundContrastAbsSum+=Math.abs(value-background);
          if(xx>0){stats.horizontalPairs++;stats.horizontalLumaGradientSum+=Math.abs(luma(data,p)-luma(data,p-4));}
          if(im)im.data.set(data.subarray(p,p+4),(yy*rw+xx)*4);
          if(side===0){
            changedPixels+=data[p]!==outputs[1][p]||data[p+1]!==outputs[1][p+1]||data[p+2]!==outputs[1][p+2];
            lumaDifferenceSum+=Math.abs(luma(data,p)-luma(outputs[1],p));
            if(Math.abs(luma(data,p)-background)>25)glyphSamples.push([x+xx,y+yy]);
          }
        }
        if(im)contexts[side].putImageData(im,0,n*slot);
        for(const k of ['pixels','chromaSum','chromaAtLeast8','horizontalPairs','horizontalLumaGradientSum','lumaOutsideSourceByOver1','backgroundContrastAbsSum'])totals[side][k]+=stats[k];
        totals[side].maxLumaOvershoot=Math.max(totals[side].maxLumaOvershoot,stats.maxLumaOvershoot);
        totals[side].maxChroma=Math.max(totals[side].maxChroma,stats.maxChroma);totals[side].alphaMin=Math.min(totals[side].alphaMin,stats.alphaMin);totals[side].alphaMax=Math.max(totals[side].alphaMax,stats.alphaMax);
      }
      for(let k=0;k<12&&glyphSamples.length;k++)samples.push(glyphSamples[Math.min(glyphSamples.length-1,Math.floor(k*glyphSamples.length/12))]);
      samples.push([x+rw-3,y+rh-3]);rows.push(row);
    }
    for(const [x,y] of samples)for(let side=0;side<2;side++){
      const method=side?'hq-stable':'sbms',expected=l.filter.sampleAt(inputs[side],d.sourceWidth,d.sourceHeight,w,h,x,y,m,method).map(v=>Math.round(v*255));
      const p=((h-1-y)*w+x)*4,error=Math.max(...expected.map((v,k)=>Math.abs(v-outputs[side][p+k])));
      cpu[side?'finalMax':'sbmsMax']=Math.max(cpu[side?'finalMax':'sbmsMax'],error);cpu.samples++;
      if(error>1&&cpu.mismatches.length<16)cpu.mismatches.push({x,y,side:side?'final':'sbms',expected,actual:Array.from(outputs[side].slice(p,p+3)),error});
    }
    const wipe=l.pixels(0);
    for(const row of rows)for(let yy=0;yy<row.output.height;yy++)for(let xx=0;xx<row.output.width;xx++){
      const x=row.output.x+xx,y=row.output.y+yy,p=((h-1-y)*w+x)*4,e=x+.5<w*l.state.split?outputs[0]:outputs[1];for(let k=0;k<4;k++)wipeErrors+=wipe[p+k]!==e[p+k];
    }
    const ratio=(before,after)=>before===0?null:100*(1-after/before);
    const result={label,asset:a.id,dpi:a.dpi,scenario:l.state.scenario,dimensions:{...d},sourceMotion:m,sourceTile:tile,nativeBypass,rows,metrics:{sbms:totals[0],final:totals[1]},improvementPercent:{chromaSum:ratio(totals[0].chromaSum,totals[1].chromaSum),chromaAtLeast8:ratio(totals[0].chromaAtLeast8,totals[1].chromaAtLeast8),maxChroma:ratio(totals[0].maxChroma,totals[1].maxChroma)},inkChangePercent:totals[0].backgroundContrastAbsSum?100*(totals[1].backgroundContrastAbsSum/totals[0].backgroundContrastAbsSum-1):null,changedPixels,meanAbsoluteLumaDifference:lumaDifferenceSum/totals[0].pixels,cpu,wipeErrors,imageSize:[width,height]};
    if(exportImages){
      finalAtlas.push({scenario:l.state.scenario,width,height,canvases});
    }
    return result;
  };
  window.exportFinalAtlas=()=>{
    const panelWidth=Math.max(...finalAtlas.map(x=>x.width)),panelHeight=Math.max(...finalAtlas.map(x=>x.height)),gap=8;
    const c=document.createElement('canvas');c.width=panelWidth*2+gap;c.height=finalAtlas.length*(panelHeight+gap)-gap;
    const ctx=c.getContext('2d');ctx.fillStyle='#ece8ef';ctx.fillRect(0,0,c.width,c.height);
    const panels=[];
    finalAtlas.forEach((entry,n)=>entry.canvases.forEach((image,side)=>{
      const x=side*(panelWidth+gap),y=n*(panelHeight+gap);ctx.drawImage(image,x,y);
      panels.push({scenario:entry.scenario,side:side?'final':'sbms',x,y,width:entry.width,height:entry.height});
    }));
    return {width:c.width,height:c.height,panels,url:c.toDataURL()};
  };
  window.checkNativeReferences=()=>{
    const l=dynamicLab,d=l.dimensions,raw=finalCase.raw,source=l.source,results=[];
    l.setState({time:0});
    for(const reference of ['sbms','raw','legacy','previous']){
      const select=document.querySelector('#dynamic-reference');select.value=reference;select.dispatchEvent(new Event('change',{bubbles:true}));
      const right=l.pixels(2);let mismatch=0;
      for(let y=0;y<d.height;y++)for(let x=0;x<d.width;x++){
        const p=((d.height-1-y)*d.width+x)*4,q=(y*d.width+x)*4;
        for(let k=0;k<4;k++)mismatch+=right[p+k]!==raw[q+k];
      }
      results.push({reference,mismatch,sourceUnchanged:source===l.source,time:l.state.time});
    }
    l.setState({reference:'sbms'});return results;
  };
  window.checkProtectedIcon=()=>{
    const l=dynamicLab,d=l.dimensions,r=l.corpus.regions.reduce((best,r)=>Math.hypot(r.x+r.width/2-d.sourceWidth/2,r.y+r.height/2-d.sourceHeight/2)<Math.hypot(best.x+best.width/2-d.sourceWidth/2,best.y+best.height/2-d.sourceHeight/2)?r:best);
    const raw=l.source.getContext('2d').getImageData(0,0,d.sourceWidth,d.sourceHeight).data,clean=l.cleanPixels(),a=l.pixels(1),b=l.pixels(2);
    let sourceChanges=0,outputChanges=0,maxDelta=0;
    for(let y=r.y;y<r.y+r.height;y++)for(let x=r.x;x<r.x+r.width;x++){const p=(y*d.sourceWidth+x)*4;for(let k=0;k<3;k++)sourceChanges+=raw[p+k]!==clean[p+k];}
    const sx=d.width/d.sourceWidth,sy=d.height/d.sourceHeight;
    for(let y=Math.floor(r.y*sy);y<Math.ceil((r.y+r.height)*sy);y++)for(let x=Math.floor(r.x*sx);x<Math.ceil((r.x+r.width)*sx);x++){const p=((d.height-1-y)*d.width+x)*4;for(let k=0;k<3;k++){outputChanges+=a[p+k]!==b[p+k];maxDelta=Math.max(maxDelta,Math.abs(a[p+k]-b[p+k]));}}
    return {state:{...l.state},region:r,sourceChanges,outputChanges,maxDelta,comparison:'HQ raw vs HQ final; SBMS is excluded'};
  };
}
(async()=>{
  fs.mkdirSync(outRoot,{recursive:true});
  const server=net.createServer();await new Promise(r=>server.listen(0,'127.0.0.1',r));const port=server.address().port;await new Promise(r=>server.close(r));
  const proc=spawn(chrome,[`--remote-debugging-port=${port}`,'--remote-debugging-address=127.0.0.1','--use-angle=d3d11','--no-first-run','about:blank'],{windowsHide:true,stdio:['ignore','ignore','pipe']});
  let ws,stderr='';proc.stderr.on('data',d=>stderr+=d);
  try{
    let pages;for(let n=0;n<150;n++){try{pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();if(pages.length)break;}catch{}await delay(100);}
    assert(pages?.length,stderr);ws=new WebSocket(pages[0].webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j;});
    let id=0;const pending=new Map(),exceptions=[],requests=[];
    ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}else if(m.method==='Runtime.exceptionThrown')exceptions.push(m.params);else if(m.method==='Network.requestWillBeSent')requests.push(m.params.request.url);};
    const send=(method,params={})=>new Promise((resolve,reject)=>{const serial=++id;pending.set(serial,{resolve,reject});ws.send(JSON.stringify({id:serial,method,params}));});
    const ev=async expression=>{const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
    const set=s=>ev(`dynamicLab.setState(${JSON.stringify(s)})`);
    const change=(id,value)=>ev(`(()=>{const e=document.querySelector('#${id}');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    const resize=async(width,height)=>{await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await delay(150);};
    const layout=()=>ev(`(()=>{const d=document.documentElement,p=document.querySelector('#settings-panel'),r=p.getBoundingClientRect();return {viewport:[innerWidth,innerHeight],scroll:[d.scrollWidth-d.clientWidth,d.scrollHeight-d.clientHeight],settings:{visible:!p.hidden,left:r.left,right:r.right,bottom:r.bottom,scroll:[p.scrollWidth-p.clientWidth,p.scrollHeight-p.clientHeight]},labels:Array.from(document.querySelectorAll('.algorithm-tag')).map(e=>e.scrollWidth-e.clientWidth)};})()`);
    const validLayout=r=>{assert(r.scroll.every(v=>v<=1));assert(r.labels.every(v=>v<=1));if(r.settings.visible){assert(r.settings.left>=0&&r.settings.right<=r.viewport[0]&&r.settings.bottom<=r.viewport[1]);assert(r.settings.scroll.every(v=>v<=1));}};
    const shot=async name=>{await delay(100);const s=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false}),file='demo-'+name+'.png';fs.writeFileSync(path.join(outRoot,file),Buffer.from(s.data,'base64'));report.screenshots.push(file);exportsManifest.screenshots.push(file);save();};
    const check=(ok,message)=>{if(!ok)report.failures.push(message);};
    await send('Runtime.enable');await send('Page.enable');await send('Network.enable');await resize(1920,1080);
    await send('Page.navigate',{url:pathToFileURL(path.join(root,'dynamic.html')).href});
    for(let n=0;n<400;n++){if(await ev('document.body?.dataset.ready==="true"'))break;await delay(100);}
    assert.equal(await ev('document.body.dataset.ready'),'true');await ev(`(${installProbe.toString()})()`);
    report.renderer=await ev(`(()=>{const g=dynamicLab.gl,e=g.getExtension('WEBGL_debug_renderer_info');return e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER);})()`);assert(!/swiftshader|llvmpipe/i.test(report.renderer));
    report.defaults=await ev('({...dynamicLab.state})');assert.equal(report.defaults.reference,'previous');assert.equal(report.defaults.content,'windows-gdi-adaptive-cleartype');assert.equal(report.defaults.scenario,'up2');
    report.desktop=await layout();validLayout(report.desktop);await shot('default');
    await ev('document.querySelector("#settings").click()');report.settings=await layout();validLayout(report.settings);await shot('settings');await ev('document.querySelector("#settings").click()');
    await resize(430,932);report.narrow=await layout();validLayout(report.narrow);await shot('narrow');await resize(1920,1080);
    await set({playing:false,focus:false,reference:'sbms',time:0,speed:72});
    for(const kind of ['cleartype','grayscale']){
      await change('content','windows-gdi-adaptive-'+kind);
      for(const scenario of ['native','up2','up15','down75','down50','down33','6k2k','x75']){
        await change('scenario',scenario);const prepared=await ev('prepareFinalCase()');
        report.timerQueryAvailable=prepared.timerQueryAvailable;
        const dx=prepared.dimensions.width/prepared.dimensions.sourceWidth,dy=prepared.dimensions.height/prepared.dimensions.sourceHeight;
        for(const motion of [[0,0],[1,0],[1,1]]){
          const phase=motion.join('-'),time=motion[0]===0?0:(motion[1]===0?dx:Math.max(dx,dy))/72+1e-8;
          await set({time,motion:motion[1]===0?'x':'xy'});
          assert.deepEqual(await ev('dynamicLab.motion'),motion);
          const shouldExport=kind==='cleartype'&&motion[0]===0;
          const result=await ev(`inspectFinalPhase(${JSON.stringify(kind+'/'+scenario+'/'+phase)},${shouldExport})`);
          if(shouldExport)exportsManifest.cases.push({scenario,kind,asset:result.asset,dpi:result.dpi,dimensions:result.dimensions,sourceMotion:motion,sourceTile:result.sourceTile,rows:result.rows,imageSize:result.imageSize});
          report.cases.push(result);
          check(result.cpu.sbmsMax<=1,result.label+': SBMS CPU/GPU '+result.cpu.sbmsMax);check(result.cpu.finalMax<=1,result.label+': final CPU/GPU '+result.cpu.finalMax);check(result.wipeErrors===0,result.label+': split mismatch');
          if(kind==='grayscale')check(result.metrics.sbms.maxChroma===0&&result.metrics.final.maxChroma===0,result.label+': grayscale gained chroma');
          if(scenario==='native')check(result.changedPixels===0,result.label+': native bypass differs');
          save();
        }
        if(scenario==='native'&&kind==='cleartype'){
          report.nativeReferences=await ev('checkNativeReferences()');
          check(report.nativeReferences.every(r=>r.mismatch===0&&r.sourceUnchanged&&r.time===0),'Native right bypass changes with reference');
        }
        const group=report.cases.filter(c=>c.scenario===scenario&&c.label.startsWith(kind+'/')),range=key=>{
          const v=group.map(c=>c.improvementPercent[key]).filter(v=>v!==null);return v.length?{min:Math.min(...v),max:Math.max(...v),mean:v.reduce((a,b)=>a+b,0)/v.length}:null;
        };
        const summary={kind,scenario,dpi:prepared.dpi,chromaSumImprovement:range('chromaSum'),chromaAtLeast8Improvement:range('chromaAtLeast8'),maxChromaImprovement:range('maxChroma')};report.summary.push(summary);save();console.log(JSON.stringify(summary));
      }
    }
    const atlas=await ev('exportFinalAtlas()');fs.writeFileSync(path.join(outRoot,'ct-atlas.png'),Buffer.from(atlas.url.split(',')[1],'base64'));delete atlas.url;
    exportsManifest.atlas={file:'ct-atlas.png',...atlas};
    await set({content:'tiktok',assetSize:48,background:'#181818',scenario:'up2',reference:'raw',playing:false,focus:false,time:0});
    report.protectedIcon=await ev('checkProtectedIcon()');check(report.protectedIcon.sourceChanges===0&&report.protectedIcon.outputChanges===0,'TikTok48 prior protection changed');
    report.dynamicErrors=await ev('dynamicLab.errors');report.browserErrors=exceptions;report.networkRequests=requests.filter(u=>/^https?:/.test(u));
    check(report.dynamicErrors.length===0,'dynamic error');check(report.browserErrors.length===0,'browser error');check(report.networkRequests.length===0,'network request');
    report.performanceNote='Timer extension availability only. No timed GPU pass or production FPS claim.';
    report.metricNotes=['Luma bounds come from each same-DPI source row, never another DPI capture. Values outside those bounds by more than 1 LSB measure ringing without a visual-quality assertion.','Ink is the sum of absolute luma difference from the row background; its percentage change describes weight, not sharpness.','All measured glyph rows are inside complete copied-edge tiles. Browser texture repeat is not production D3D clamp; these measurements are not native D3D bit-for-bit validation.'];
    save();console.log('DONE '+report.cases.length+' phase cases; '+report.failures.length+' failures');if(report.failures.length)process.exitCode=1;
    await send('Browser.close').catch(()=>{});
  }catch(error){report.errors.push(error.stack||String(error));save();console.error(error);process.exitCode=1;}finally{if(ws&&ws.readyState===WebSocket.OPEN)ws.close();if(proc.exitCode===null)proc.kill();}
})();
