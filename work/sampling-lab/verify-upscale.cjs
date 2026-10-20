// Browser GPU regression check for separated upscale luma/chroma.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{pathToFileURL}=require('node:url');
const root=__dirname,out=path.join(root,'upscale-exports'),delay=ms=>new Promise(r=>setTimeout(r,ms));
const chrome='C:/Users/EZsTp/AppData/Local/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-win64/chrome-headless-shell.exe';
const report={scope:'Actual browser GPU, fixed native GDI text inputs, SBMS mathematical reproduction versus previous RGB EWA versus new EWA luma/bilinear chroma. Not native SBMS, codec or performance testing.',text:[],color:[],unchanged:[],history:[],screenshots:[],failures:[],errors:[]};
const save=()=>fs.writeFileSync(path.join(root,'upscale-verification.json'),JSON.stringify(report,null,2));
function installProbe(){
  const L=()=>dynamicLab;
  window.upscaleTextImages=[];window.upscaleColorImages=[];
  // Native images were already decoded by the application. Expose individual
  // immutable variants only inside this test tab to separate source DPI from ratio.
  for(const group of L().corpus.assets.filter(a=>a.variants))for(const a of group.variants)if(!L().corpus.assets.some(b=>b.id===a.id))L().corpus.assets.push(a);
  const y=(a,p)=>.2126*a[p]+.7152*a[p+1]+.0722*a[p+2];
  const c=(a,p)=>Math.max(a[p],a[p+1],a[p+2])-Math.min(a[p],a[p+1],a[p+2]);
  const blank=()=>({pixels:0,chromaSum:0,chromaAtLeast8:0,maxChroma:0});
  const measure=(a,p,s)=>{const v=c(a,p);s.pixels++;s.chromaSum+=v;s.chromaAtLeast8+=v>=8;s.maxChroma=Math.max(s.maxChroma,v);};
  const near=()=>{const l=L(),d=l.dimensions;return l.corpus.regions.reduce((a,b)=>Math.hypot(a.x+a.width/2-d.sourceWidth/2,a.y+a.height/2-d.sourceHeight/2)<Math.hypot(b.x+b.width/2-d.sourceWidth/2,b.y+b.height/2-d.sourceHeight/2)?a:b);};
  window.prepareUpscale=()=>{
    const l=L(),d=l.dimensions;window.upcase={source:l.source,raw:l.source.getContext('2d').getImageData(0,0,d.sourceWidth,d.sourceHeight).data,clean:l.cleanPixels(),asset:l.corpus.activeAsset,tile:near()};
    return {asset:upcase.asset.id,dimensions:{...d},dpi:upcase.asset.dpi,source:upcase.tile};
  };
  const read=()=>{
    const l=L(),before={time:l.state.time,motion:l.motion,source:l.source};l.setState({reference:'previous'});const previous=l.pixels(1),current=l.pixels(2);
    l.setState({reference:'sbms'});const sbms=l.pixels(1),currentAgain=l.pixels(2);l.setState({reference:'previous'});
    let referenceChanges=0;for(let p=0;p<current.length;p++)referenceChanges+=current[p]!==currentAgain[p];
    return {outputs:{sbms,previous,current},referenceChanges,sourceUnchanged:before.source===l.source&&l.source===upcase.source,timeUnchanged:before.time===l.state.time,motionUnchanged:JSON.stringify(before.motion)===JSON.stringify(l.motion)};
  };
  const crop=(data,r,w,h)=>{
    const v=document.createElement('canvas');v.width=r.width;v.height=r.height;const ctx=v.getContext('2d'),im=ctx.createImageData(v.width,v.height);
    for(let yy=0;yy<v.height;yy++)for(let xx=0;xx<v.width;xx++){const p=((h-1-r.y-yy)*w+r.x+xx)*4;im.data.set(data.subarray(p,p+4),(yy*v.width+xx)*4);}
    ctx.putImageData(im,0,0);return v;
  };
  const rect=r=>{const l=L(),d=l.dimensions,m=l.motion,sx=d.width/d.sourceWidth,sy=d.height/d.sourceHeight,x=Math.floor((r.x-m[0])*sx),yy=Math.floor((r.y-m[1])*sy);return {x,y:yy,width:Math.ceil((r.x+r.width-m[0])*sx)-x,height:Math.ceil((r.y+r.height-m[1])*sy)-yy};};
  const cpu=(outputs,points)=>{
    const l=L(),d=l.dimensions,res={samples:0,sbmsMax:0,previousMax:0,currentMax:0,mismatches:[]};
    for(const [x,y] of points)for(const [name,method,input] of [['sbms','sbms',upcase.raw],['previous','hq',upcase.clean],['current','hq-stable',upcase.clean]]){
      const expected=l.filter.sampleAt(input,d.sourceWidth,d.sourceHeight,d.width,d.height,x,y,l.motion,method).map(v=>Math.round(v*255)),p=((d.height-1-y)*d.width+x)*4;
      const error=Math.max(...expected.map((v,k)=>Math.abs(v-outputs[name][p+k])));res.samples++;res[name+'Max']=Math.max(res[name+'Max'],error);
      if(error>1&&res.mismatches.length<12)res.mismatches.push({x,y,name,error,expected,actual:Array.from(outputs[name].slice(p,p+3))});
    }
    return res;
  };
  window.inspectUpscaleText=exportImage=>{
    const l=L(),d=l.dimensions,{outputs,...checks}=read(),tile=upcase.tile,a=upcase.asset,rows=[],points=[],totals={sbms:blank(),previous:blank(),current:blank()},patches=[];
    let changed=0,lumaSum=0,lumaMax=0,maxChannelDelta=0,wipeErrors=0;const wipe=l.pixels(0);
    for(const r of a.regions){
      const src={x:tile.x+r.x,y:tile.y+r.y,width:r.width,height:r.height},o=rect(src),stats={sbms:blank(),previous:blank(),current:blank()},glyphs=[];
      if(o.x<0||o.y<0||o.x+o.width>d.width||o.y+o.height>d.height)throw Error('Incomplete text row');
      for(let yy=0;yy<o.height;yy++)for(let xx=0;xx<o.width;xx++){
        const px=o.x+xx,py=o.y+yy,p=((d.height-1-py)*d.width+px)*4;
        for(const name of Object.keys(outputs))measure(outputs[name],p,stats[name]);
        const delta=Math.abs(y(outputs.previous,p)-y(outputs.current,p));lumaSum+=delta;lumaMax=Math.max(lumaMax,delta);
        for(let k=0;k<3;k++)maxChannelDelta=Math.max(maxChannelDelta,Math.abs(outputs.previous[p+k]-outputs.current[p+k]));
        changed+=outputs.previous[p]!==outputs.current[p]||outputs.previous[p+1]!==outputs.current[p+1]||outputs.previous[p+2]!==outputs.current[p+2];
        const expected=px+.5<d.width*l.state.split?outputs.previous:outputs.current;for(let k=0;k<4;k++)wipeErrors+=wipe[p+k]!==expected[p+k];
        if(Math.abs(y(outputs.previous,p)-(r.background==='#ffffff'?255:24))>25)glyphs.push([px,py]);
      }
      for(const name of Object.keys(totals)){for(const k of ['pixels','chromaSum','chromaAtLeast8'])totals[name][k]+=stats[name][k];totals[name].maxChroma=Math.max(totals[name].maxChroma,stats[name].maxChroma);}
      for(let k=0;k<8&&glyphs.length;k++)points.push(glyphs[Math.floor(k*glyphs.length/8)]);
      rows.push({id:r.id,text:r.text,source:src,output:o,fontHeightPx:r.fontHeightPx,metrics:stats});
      if(exportImage&&['light-english','light-chinese','dark-code'].includes(r.id)){
        const roi={...o,width:Math.min(540,o.width)};
        // Include trailing English digits; use leading Chinese/code. Shared ROI.
        if(r.id==='light-english'&&glyphs.length)roi.x=Math.max(o.x,Math.max(...glyphs.map(p=>p[0]))+1-roi.width);
        patches.push({id:r.id,roi,canvases:Object.values(outputs).map(v=>crop(v,roi,d.width,d.height))});
      }
    }
    if(exportImage)upscaleTextImages.push({label:`${Number(a.dpi.toFixed(3))}% / ${Math.abs(a.regions[0].fontHeightPx)} px source / ${l.state.scenario}`,patches});
    return {asset:a.id,dpi:a.dpi,sourceEm:Math.abs(a.regions[0].fontHeightPx),scenario:l.state.scenario,motion:l.motion,dimensions:{...d},metrics:totals,rows,changedPixels:changed,maxChannelDelta,lumaMeanAbsoluteDelta:lumaSum/totals.previous.pixels,lumaMaxAbsoluteDelta:lumaMax,wipeErrors,cpu:cpu(outputs,points),...checks};
  };
  window.inspectUpscaleColor=exportImage=>{
    const l=L(),d=l.dimensions,{outputs,...checks}=read(),o=rect(upcase.tile),stats={sbms:blank(),previous:blank(),current:blank()},points=[];let changed=0,lumaSum=0,lumaMax=0,rgbSum=0,maxDelta=0,oldStrongChroma=0,currentStrongChroma=0;
    for(let yy=0;yy<o.height;yy++)for(let xx=0;xx<o.width;xx++){
      const p=((d.height-1-o.y-yy)*d.width+o.x+xx)*4;for(const name of Object.keys(outputs))measure(outputs[name],p,stats[name]);
      const dy=Math.abs(y(outputs.previous,p)-y(outputs.current,p));lumaSum+=dy;lumaMax=Math.max(lumaMax,dy);
      if(c(outputs.previous,p)>=32){oldStrongChroma+=c(outputs.previous,p);currentStrongChroma+=c(outputs.current,p);}
      let ch=false;for(let k=0;k<3;k++){const v=Math.abs(outputs.previous[p+k]-outputs.current[p+k]);rgbSum+=v;maxDelta=Math.max(maxDelta,v);ch||=v!==0;}changed+=ch;
    }
    for(let yy=0;yy<7;yy++)for(let xx=0;xx<7;xx++)points.push([o.x+Math.floor((xx+.5)*o.width/7),o.y+Math.floor((yy+.5)*o.height/7)]);
    if(exportImage)upscaleColorImages.push({label:`${upcase.asset.id} / ${l.state.assetSize} px / ${l.state.scenario} / ${l.state.background}`,roi:o,canvases:Object.values(outputs).map(v=>crop(v,o,d.width,d.height))});
    return {asset:upcase.asset.id,scenario:l.state.scenario,size:l.state.assetSize,background:l.state.background,motion:l.motion,region:o,metrics:stats,changedPixels:changed,lumaMeanAbsoluteDelta:lumaSum/stats.previous.pixels,lumaMaxAbsoluteDelta:lumaMax,rgbMeanAbsoluteDelta:rgbSum/(3*stats.previous.pixels),maxChannelDelta:maxDelta,strongColorChromaReductionPercent:oldStrongChroma?100*(1-currentStrongChroma/oldStrongChroma):null,cpu:cpu(outputs,points),...checks};
  };
  window.exportUpscaleMontage=kind=>{
    const entries=kind==='text'?upscaleTextImages:upscaleColorImages,gap=12,header=30,panel=560;
    const heights=entries.map(e=>header+(e.patches?e.patches.reduce((sum,p)=>sum+p.roi.height+gap,0):e.roi.height+gap)),v=document.createElement('canvas');v.width=3*panel+4*gap;v.height=40+heights.reduce((a,b)=>a+b,0);
    const ctx=v.getContext('2d');ctx.fillStyle='#ece8ef';ctx.fillRect(0,0,v.width,v.height);ctx.font='15px sans-serif';ctx.fillStyle='#202026';
    ['SBMS current','Previous final','Luma EWA / chroma bilinear'].forEach((label,n)=>ctx.fillText(label,gap+n*(panel+gap),24));
    let top=40;const metadata=[];
    for(let n=0;n<entries.length;n++){
      const e=entries[n];ctx.fillStyle='#202026';ctx.fillText(e.label,gap,top+19);let yy=top+header;const patches=e.patches||[e];
      for(const p of patches){for(let side=0;side<3;side++)ctx.drawImage(p.canvases[side],gap+side*(panel+gap),yy);metadata.push({label:e.label,id:p.id,sourceOutputRoi:p.roi,x:gap,y:yy,width:p.roi.width,height:p.roi.height,columnStride:panel+gap});yy+=p.roi.height+gap;}
      top+=heights[n];
    }
    return {width:v.width,height:v.height,panels:metadata,url:v.toDataURL()};
  };
  window.checkUnchangedUpscale=()=>{
    const l=L();l.setState({reference:'previous',time:0});const a=l.pixels(1),b=l.pixels(2);let changes=0,maxDelta=0;for(let p=0;p<a.length;p++){changes+=a[p]!==b[p];maxDelta=Math.max(maxDelta,Math.abs(a[p]-b[p]));}return {scenario:l.state.scenario,content:l.state.content,channels:a.length,changes,maxDelta};
  };
  window.checkAllReferences=()=>{
    const l=L(),source=l.source,time=l.state.time,motion=l.motion,results=[];l.setState({reference:'previous'});const expected=l.pixels(2);
    for(const reference of ['previous','sbms','raw','legacy']){l.setState({reference});const b=l.pixels(2);let changes=0;for(let p=0;p<b.length;p++)changes+=expected[p]!==b[p];results.push({reference,changes,sourceUnchanged:source===l.source,timeUnchanged:time===l.state.time,motionUnchanged:JSON.stringify(motion)===JSON.stringify(l.motion)});}
    l.setState({reference:'previous'});return results;
  };
}
(async()=>{
  fs.mkdirSync(out,{recursive:true});const server=net.createServer();await new Promise(r=>server.listen(0,'127.0.0.1',r));const port=server.address().port;await new Promise(r=>server.close(r));
  const proc=spawn(chrome,[`--remote-debugging-port=${port}`,'--remote-debugging-address=127.0.0.1','--use-angle=d3d11','--no-first-run','about:blank'],{windowsHide:true,stdio:['ignore','ignore','pipe']});let ws,stderr='';proc.stderr.on('data',d=>stderr+=d);
  try{
    let pages;for(let n=0;n<150;n++){try{pages=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();if(pages.length)break;}catch{}await delay(100);}assert(pages?.length,stderr);
    ws=new WebSocket(pages[0].webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j;});let id=0;const pending=new Map(),exceptions=[],requests=[];
    ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}else if(m.method==='Runtime.exceptionThrown')exceptions.push(m.params);else if(m.method==='Network.requestWillBeSent')requests.push(m.params.request.url);};
    const send=(method,params={})=>new Promise((resolve,reject)=>{const serial=++id;pending.set(serial,{resolve,reject});ws.send(JSON.stringify({id:serial,method,params}));});
    const ev=async expression=>{const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
    const set=s=>ev(`dynamicLab.setState(${JSON.stringify(s)})`),check=(ok,msg)=>{if(!ok)report.failures.push(msg);};
    const resize=async(width,height)=>{await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await delay(150);};
    const shot=async name=>{const r=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});fs.writeFileSync(path.join(out,name+'.png'),Buffer.from(r.data,'base64'));report.screenshots.push(name+'.png');save();};
    const layout=()=>ev(`(()=>{const d=document.documentElement,p=document.querySelector('#settings-panel'),r=p.getBoundingClientRect();return {viewport:[innerWidth,innerHeight],scroll:[d.scrollWidth-d.clientWidth,d.scrollHeight-d.clientHeight],settings:{visible:!p.hidden,left:r.left,right:r.right,bottom:r.bottom,scroll:[p.scrollWidth-p.clientWidth,p.scrollHeight-p.clientHeight]},labels:Array.from(document.querySelectorAll('.algorithm-tag')).map(e=>e.scrollWidth-e.clientWidth)};})()`);
    const verifyLayout=r=>{check(r.scroll.every(v=>v<=1),'Page scroll');check(r.labels.every(v=>v<=1),'Label overflow');if(r.settings.visible)check(r.settings.left>=0&&r.settings.right<=r.viewport[0]&&r.settings.bottom<=r.viewport[1]&&r.settings.scroll.every(v=>v<=1),'Settings overflow');};
    const navigate=async file=>{await send('Page.navigate',{url:pathToFileURL(path.join(root,file)).href});for(let n=0;n<400;n++){if(await ev('document.body?.dataset.ready==="true"'))break;await delay(100);}assert.equal(await ev('document.body.dataset.ready'),'true');};
    await send('Runtime.enable');await send('Page.enable');await send('Network.enable');await resize(1920,1080);await navigate('dynamic.html');await ev(`(${installProbe.toString()})()`);
    report.renderer=await ev(`(()=>{const g=dynamicLab.gl,e=g.getExtension('WEBGL_debug_renderer_info');return e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER);})()`);check(!/swiftshader|llvmpipe/i.test(report.renderer),'Software renderer');
    report.defaults=await ev('({...dynamicLab.state})');check(report.defaults.scenario==='up2'&&report.defaults.reference==='previous'&&report.defaults.content==='windows-gdi-adaptive-cleartype'&&report.defaults.focus,'Wrong defaults');
    report.desktop=await layout();verifyLayout(report.desktop);await shot('desktop');
    await ev('document.querySelector("#settings").click()');report.settings=await layout();verifyLayout(report.settings);await shot('settings');await ev('document.querySelector("#settings").click()');
    await resize(430,932);report.narrow=await layout();verifyLayout(report.narrow);await shot('narrow');await resize(1920,1080);
    const validate=(r,label)=>{for(const name of ['sbms','previous','current'])check(r.cpu[name+'Max']<=1,label+' '+name+' CPU/GPU error '+r.cpu[name+'Max']);check(r.referenceChanges===0&&r.sourceUnchanged&&r.timeUnchanged&&r.motionUnchanged,label+' reference/source stability');};
    await set({playing:false,focus:false,speed:72,reference:'previous'});
    for(const kind of ['cleartype','grayscale'])for(const [dpi,id] of [[50,'windows-gdi-auto-50'],[66.6667,'windows-gdi-auto-66_667'],[100,'windows-gdi-100'],[125,'windows-gdi-125']])for(const scenario of ['up2','up15']){
      const content=id+'-'+kind;await set({scenario,content,time:0});const prepared=await ev('prepareUpscale()'),d=prepared.dimensions,scale=d.width/d.sourceWidth;
      for(const motion of [[0,0],[1,0],[1,1]]){
        await set({motion:motion[1]?'xy':'x',time:motion[0]?scale/72+1e-8:0});assert.deepEqual(await ev('dynamicLab.motion'),motion);
        const r=await ev(`inspectUpscaleText(${kind==='cleartype'&&scenario==='up2'&&motion[0]===0})`);report.text.push(r);const label=content+'/'+scenario+'/'+motion.join(',');validate(r,label);check(r.wipeErrors===0,label+' wipe');
        if(kind==='grayscale')check(r.changedPixels===0&&r.metrics.current.maxChroma===0,label+' grayscale changed');save();
      }
      console.log(`text ${kind} ${dpi}% ${scenario}`);
    }
    for(const asset of ['tiktok','vlc','firefox','git','vscode','rainbow-flag','astronaut'])for(const size of (asset==='astronaut'?[256]:[16,48]))for(const background of ['#181818','#ffffff'])for(const scenario of ['up2','up15']){
      await set({content:asset,assetSize:size,background,scenario,time:0,motion:'x'});await ev('prepareUpscale()');
      const exportImage=scenario==='up2'&&(asset==='tiktok'&&size===16&&background==='#ffffff'||background==='#181818'&&(size===48||asset==='astronaut')),r=await ev(`inspectUpscaleColor(${exportImage})`);
      report.color.push(r);validate(r,`${asset}/${size}/${background}/${scenario}`);save();
      console.log(`color ${asset} ${size} ${background} ${scenario}`);
    }
    for(const kind of ['cleartype','grayscale'])for(const scenario of ['native','down75','down50','down33','6k2k','x75']){
      await set({content:'windows-gdi-adaptive-'+kind,scenario,time:0,reference:'previous'});const r=await ev('checkUnchangedUpscale()');report.unchanged.push(r);check(r.changes===0,kind+'/'+scenario+' outside-upscale changed');save();
    }
    await set({content:'windows-gdi-125-cleartype',scenario:'up2',time:2.25,motion:'xy'});report.references=await ev('checkAllReferences()');check(report.references.every(r=>r.changes===0&&r.sourceUnchanged&&r.timeUnchanged&&r.motionUnchanged),'Reference switching changes right');
    await ev('window.playProbe={source:dynamicLab.source,time:dynamicLab.state.time,frames:dynamicLab.frameCount};dynamicLab.setState({playing:true})');await delay(400);await set({playing:false});
    report.playback=await ev('({sourceUnchanged:playProbe.source===dynamicLab.source,elapsed:dynamicLab.state.time-playProbe.time,frames:dynamicLab.frameCount-playProbe.frames,motion:dynamicLab.motion})');check(report.playback.sourceUnchanged&&report.playback.elapsed>0&&report.playback.frames>1,'Playback did not advance');
    report.exports={};for(const kind of ['text','color']){const r=await ev(`exportUpscaleMontage('${kind}')`);fs.writeFileSync(path.join(out,kind+'-comparison.png'),Buffer.from(r.url.split(',')[1],'base64'));delete r.url;report.exports[kind]={file:kind+'-comparison.png',...r};}
    report.dynamicErrors=await ev('dynamicLab.errors');check(report.dynamicErrors.length===0,'Dynamic errors');
    for(const file of ['attack.html','real-attack.html','refined.html']){
      await navigate(file);const r=await ev(`(()=>{const l=dynamicLab,g=l.gl,p=g.getParameter(g.CURRENT_PROGRAM),u=g.getUniformLocation(p,'u_upscaleChroma');return {ready:document.body.dataset.ready,state:{...l.state},errors:l.errors,upscaleUniform:u?g.getUniform(p,u):null,frameCount:l.frameCount};})()`);r.file=file;report.history.push(r);check(r.ready==='true'&&r.errors.length===0&&r.upscaleUniform===0,file+' history regression');save();
    }
    report.browserErrors=exceptions;report.networkRequests=requests.filter(u=>/^https?:/.test(u));check(!exceptions.length,'Browser exceptions');check(!report.networkRequests.length,'Network access');
    const range=values=>({min:Math.min(...values),max:Math.max(...values),mean:values.reduce((a,b)=>a+b,0)/values.length});
    report.summary={cpuSamples:[...report.text,...report.color].reduce((sum,r)=>sum+r.cpu.samples,0),text:[],color:[]};
    for(const sourceEm of [6,8,12,15])for(const scenario of ['up2','up15']){
      const group=report.text.filter(r=>r.sourceEm===sourceEm&&r.scenario===scenario&&r.asset.endsWith('cleartype'));
      report.summary.text.push({sourceEm,scenario,previousChromaReductionVsSbms:range(group.map(r=>100*(1-r.metrics.previous.chromaSum/r.metrics.sbms.chromaSum))),currentChromaReductionVsSbms:range(group.map(r=>100*(1-r.metrics.current.chromaSum/r.metrics.sbms.chromaSum))),currentChromaReductionVsPrevious:range(group.map(r=>100*(1-r.metrics.current.chromaSum/r.metrics.previous.chromaSum))),maxChroma:{sbms:range(group.map(r=>r.metrics.sbms.maxChroma)),previous:range(group.map(r=>r.metrics.previous.maxChroma)),current:range(group.map(r=>r.metrics.current.maxChroma))}});
    }
    for(const asset of ['tiktok','vlc','firefox','git','vscode','rainbow-flag','astronaut']){
      const group=report.color.filter(r=>r.asset===asset);
      report.summary.color.push({asset,chromaReductionVsPrevious:range(group.map(r=>100*(1-r.metrics.current.chromaSum/r.metrics.previous.chromaSum))),strongChromaReductionVsPrevious:range(group.map(r=>r.strongColorChromaReductionPercent)),lumaMeanAbsoluteDelta:range(group.map(r=>r.lumaMeanAbsoluteDelta)),lumaMaxAbsoluteDelta:Math.max(...group.map(r=>r.lumaMaxAbsoluteDelta)),maxChannelDelta:Math.max(...group.map(r=>r.maxChannelDelta))});
    }
    report.notes=['Chroma is max(R,G,B)-min(R,G,B); reduced text chroma is not a complete sharpness score. Color-image chroma reduction can be an unwanted change, not an improvement.','Luma means gamma-encoded Rec.709 Y prime. RGB gamut clipping and byte rounding may change it after recombination.','CPU comparisons use the actual GPU-preprocessed source to isolate sampling behavior. This does not revalidate source defringe against its separate scalar implementation.','Text uses all six complete captured rows at three integer source offsets. Exported text shows three representative rows at zero offset, with shared crops.','Color tests include complete central tiles at zero motion; bounded 7 by 7 CPU sampling per tile. They are finite examples, not a proof against all false positives.','Original native captures are unmodified. Individual 50 and 66.667 percent variants are temporarily exposed in this test tab.','No timing, encoding, transport, HDR or actual product D3D test.'];
    save();console.log(JSON.stringify({done:true,text:report.text.length,color:report.color.length,unchanged:report.unchanged.length,failures:report.failures}));if(report.failures.length)process.exitCode=1;await send('Browser.close').catch(()=>{});
  }catch(e){report.errors.push(e.stack||String(e));save();console.error(e);process.exitCode=1;}finally{if(ws&&ws.readyState===WebSocket.OPEN)ws.close();if(proc.exitCode===null)proc.kill();}
})();
