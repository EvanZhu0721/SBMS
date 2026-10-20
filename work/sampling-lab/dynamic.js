/* Browser-only sampling experiment. No application or display settings change. */
(() => {
  "use strict";
  const $ = id => document.getElementById(id);
  const F = window.DynamicFilter;
  const attackMode = document.body.dataset.content === "attack";
  const realMode = document.body.dataset.content === "real";
  const reviewMode = document.body.dataset.content === "review";
  const testMode = attackMode || realMode || reviewMode;
  const scenarios = {
    native: {ratio:[1,1], target:[1920,1080]},
    up2: {ratio:[2,2], target:[3840,2160]},
    up15: {ratio:[1.5,1.5], target:[3840,2160]},
    down75: {ratio:[.75,.75], target:[1920,1080]},
    down50: {ratio:[.5,.5], target:[1920,1080]},
    down33: {ratio:[1/3,1/3], target:[1920,1080]},
    "6k2k": {ratio:[5/12,5/12], target:[2560,1440]},
    x75: {ratio:[.75,1], target:[1920,1440]}
  };
  const state = {scenario:testMode?"native":"up2", split:.5, playing:false, time:0, motion:testMode?"x":"y", speed:72, dpi:"auto",focus:true,logoSize:reviewMode?128:64,sample:"both",reference:testMode?"legacy":"previous",content:testMode?"positive":"windows-gdi-adaptive-cleartype",assetSize:48,background:"#181818"};
  const errors = [];
  let gl, program, uniforms, texture, cleanTexture, cleanFramebuffer, cleanProgram, cleanUniforms;
  let source, raf, lastTime, frames=0, fpsTime=0;
  let frameCount=0, renderMode=0, resizeTimer, dimensions;
  let attackRegions=[];
  let logoRegion;
  let reviewRegions,legacyTexture;
  const logoRasters=new Map();
  const corpusAssets=JSON.parse($("corpus-data").textContent);
  const assetImages=new Map(),assetRasters=new Map();
  let corpusRegions=[];
  const canvas = $("view");
  const font = '"Segoe UI","Microsoft YaHei",sans-serif';

  function fail(error) {
    errors.push(String(error?.stack || error));
    $("error").hidden = false;
    $("error").textContent = `预览未能启动：${error.message || error}`;
    document.body.dataset.ready = "error";
    cancelAnimationFrame(raf);
  }
  window.addEventListener("error", e => fail(e.error || e.message));
  window.addEventListener("unhandledrejection", e => fail(e.reason));

  function makeCanvas(width, height) {
    const c=document.createElement("canvas");
    c.width=width;c.height=height;
    return c;
  }
  function label(ctx, text, x, y, size=13, color="#302b35", weight=400) {
    ctx.font=`${weight} ${size}px ${font}`;
    ctx.textBaseline="top";ctx.fillStyle=color;
    ctx.fillText(text,x,y);
  }
  function rule(ctx,x,y,w,color="#ddd7e0") {
    ctx.fillStyle=color;ctx.fillRect(x,y,w,1);
  }
  function tilePattern(ctx, kind) {
    const p=makeCanvas(kind==="checker"?4:2,kind==="checker"?4:2);
    const c=p.getContext("2d");
    c.fillStyle="#f5f5f5";c.fillRect(0,0,p.width,p.height);
    c.fillStyle="#101010";
    if(kind==="checker"){c.fillRect(0,0,2,2);c.fillRect(2,2,2,2);}
    else c.fillRect(0,0,1,2);
    return ctx.createPattern(p,"repeat");
  }
  function rgbText(ctx, w, h, density) {
    const width=Math.max(1,Math.floor(w*density)), height=Math.max(1,Math.floor(h*density));
    const mask=makeCanvas(width*3,height), m=mask.getContext("2d",{willReadFrequently:true});
    m.scale(density*3,density);
    label(m,"RGB · 显示器 012345",0,0,18,"#000",500);
    label(m,"RGB / AaBb 参考屏",0,32,12,"#000");
    label(m,"Il1  O0  rn m  清晰度",0,56,14,"#000");
    const a=m.getImageData(0,0,width*3,height).data;
    const result=makeCanvas(width,height), r=result.getContext("2d");
    const image=r.createImageData(width,height);
    for(let y=0;y<height;y++)for(let x=0;x<width;x++){
      const to=(y*width+x)*4, from=(y*width*3+x*3)*4;
      for(let k=0;k<3;k++)image.data[to+k]=255-a[from+k*4+3];
      image.data[to+3]=255;
    }
    r.putImageData(image,0,0);
    ctx.drawImage(result,0,0,w,h);
  }
  function logoRaster(size) {
    if(!logoRasters.has(size)){
      const raster=makeCanvas(size,size),r=raster.getContext("2d",{willReadFrequently:true});
      r.imageSmoothingEnabled=true;r.imageSmoothingQuality="high";
      r.drawImage($("real-logo"),0,0,size,size);
      logoRasters.set(size,r.getImageData(0,0,size,size));
    }
    return logoRasters.get(size);
  }
  function activeAsset() {
    const asset=corpusAssets.find(a=>a.id===state.content);
    // A new native GDI capture for this scale, never a resized fixed screenshot.
    // Use the vertical ratio to keep font height; x75 intentionally compresses width.
    return asset?.variants
      ?asset.variants.find(v=>Math.abs(v.dpi-100/scenarios[state.scenario].ratio[1])<.001)
      :asset;
  }
  function assetRaster(asset) {
    const key=`${asset.id}/${state.assetSize}/${state.background}`;
    if(!assetRasters.has(key)){
      const img=assetImages.get(asset.id),scale=asset.kind==="capture"?1:state.assetSize/Math.max(img.naturalWidth,img.naturalHeight);
      const width=Math.max(1,Math.round(img.naturalWidth*scale)),height=Math.max(1,Math.round(img.naturalHeight*scale));
      const rasterPad=4,pad=18,c=makeCanvas(width+rasterPad*2,height+rasterPad*2),ctx=c.getContext("2d",{willReadFrequently:true});
      ctx.fillStyle=state.background;ctx.fillRect(0,0,c.width,c.height);
      ctx.imageSmoothingEnabled=true;ctx.imageSmoothingQuality="high";
      // Match the static corpus rasterization, including its canvas dimensions
      // and draw origin; SVG edge rounding can differ with another origin.
      ctx.drawImage(img,rasterPad,rasterPad,width,height);
      const raster=ctx.getImageData(0,0,c.width,c.height);
      const padded=new ImageData(width+pad*2,height+pad*2);
      for(let y=0;y<padded.height;y++)for(let x=0;x<padded.width;x++){
        const sx=rasterPad+Math.max(0,Math.min(width-1,x-pad)),sy=rasterPad+Math.max(0,Math.min(height-1,y-pad));
        const to=(y*padded.width+x)*4,from=(sy*raster.width+sx)*4;
        for(let k=0;k<4;k++)padded.data[to+k]=raster.data[from+k];
      }
      assetRasters.set(key,{width,height,pad,padded});
    }
    return assetRasters.get(key);
  }
  function drawCorpusSource(width,height,density) {
    const asset=activeAsset(),r=assetRaster(asset);
    const c=makeCanvas(width,height),ctx=c.getContext("2d");
    ctx.fillStyle=state.background;ctx.fillRect(0,0,width,height);
    const labelSpace=Math.round(22*density);
    const cw=Math.max(Math.round(112*density),r.width+2*r.pad+Math.round(12*density));
    const ch=Math.max(Math.round(112*density),r.height+2*r.pad+labelSpace);
    corpusRegions=[];
    for(let cy=0;cy<height;cy+=ch)for(let cx=0;cx<width;cx+=cw){
      const x=cx+Math.round((cw-r.width)/2),y=cy+Math.round((ch-labelSpace-r.height)/2);
      ctx.putImageData(r.padded,x-r.pad,y-r.pad);
      label(ctx,asset.kind==="capture"?`${asset.name} · 原像素`:`${asset.name} · ${state.assetSize} px`,cx+10*density,y+r.height+r.pad+4*density,10*density,state.background==="#ffffff"?"#817783":"#a69da9");
      if(x>=r.pad&&y>=r.pad&&x+r.width+r.pad<=width&&y+r.height+r.pad<=height)
        corpusRegions.push({x,y,width:r.width,height:r.height,id:asset.id,size:state.assetSize,background:state.background});
    }
    return c;
  }
  function drawReviewSource(width,height,density) {
    const c=makeCanvas(width,height),ctx=c.getContext("2d");
    const text={x:Math.round(width/2-118*density),y:Math.round(height/2-110*density),width:Math.floor(236*density),height:Math.floor(83*density)};
    const logo={x:Math.floor((width-state.logoSize)/2),y:Math.round(height/2-15*density),width:state.logoSize,height:state.logoSize};
    ctx.fillStyle="#fff";ctx.fillRect(0,0,width,height);
    ctx.fillStyle="#000";ctx.fillRect(0,logo.y-Math.round(8*density),width,height);
    ctx.save();ctx.translate(text.x,text.y);ctx.scale(density,density);
    rgbText(ctx,236,83,density);ctx.restore();
    ctx.putImageData(logoRaster(state.logoSize),logo.x,logo.y);
    reviewRegions={text,logo};logoRegion=logo;
    return c;
  }

  function drawAttackSource(width,height,density) {
    const c=makeCanvas(width,height), ctx=c.getContext("2d");
    const margin=Math.round(24*density), top=Math.round(65*density);
    const rowHeight=Math.floor((height-top)/4);
    const kinds=["edge","edge4","control","rotated"];
    const titles=["01  黑｜1 px 红｜白","02  黑｜4 px 红｜白","03  白底红线 · 对照","04  旋转 90° · 对照"];
    ctx.fillStyle="#fff";ctx.fillRect(0,0,width,height);
    label(ctx,"真彩色误判",margin,20*density,16*density,"#302b34",600);
    label(ctx,"RGB 255, 0, 0 · 源像素",width-margin-230*density,22*density,12*density,"#817783");
    attackRegions=kinds.map((kind,i)=>{
      const y=top+i*rowHeight;
      label(ctx,titles[i],margin,y,12*density,"#63516d",500);
      const r={kind,x:margin,y:y+Math.round(26*density),width:width-margin*2,height:rowHeight-Math.round(42*density)};
      ctx.save();ctx.beginPath();ctx.rect(r.x,r.y,r.width,r.height);ctx.clip();
      // These are intentional, opaque RGB pixels. No glyph mask, subpixel
      // coverage, canvas scaling or interpolated image is involved.
      if(kind==="rotated"){
        for(let yy=r.y;yy<r.y+r.height;yy+=24){
          ctx.fillStyle="#000";ctx.fillRect(r.x,yy,r.width,12);
          ctx.fillStyle="#f00";ctx.fillRect(r.x,yy+12,r.width,1);
        }
      }else{
        for(let x=r.x;x<r.x+r.width;x+=24){
          if(kind!=="control"){ctx.fillStyle="#000";ctx.fillRect(x,r.y,12,r.height);}
          ctx.fillStyle="#f00";ctx.fillRect(x+12,r.y,kind==="edge4"?4:1,r.height);
        }
      }
      ctx.restore();
      return r;
    });
    return c;
  }
  function drawSource(width,height,density) {
    if(!testMode&&state.content!=="positive")return drawCorpusSource(width,height,density);
    corpusRegions=[];
    if(reviewMode&&state.sample==="both")return drawReviewSource(width,height,density);
    if(realMode||(reviewMode&&state.sample==="logo")){
      const c=makeCanvas(width,height),ctx=c.getContext("2d"),size=state.logoSize;
      ctx.fillStyle="#000";ctx.fillRect(0,0,width,height);
      logoRegion={x:Math.floor((width-size)/2),y:Math.floor((height-size)/2),width:size,height:size};
      // Freeze one ordinary browser raster per icon size. Changing the output
      // scenario must not rerasterize the image with a different canvas backend.
      ctx.putImageData(logoRaster(size),logoRegion.x,logoRegion.y);
      return c;
    }
    if(attackMode)return drawAttackSource(width,height,density);
    const c=makeCanvas(width,height), ctx=c.getContext("2d");
    const columns=canvas.width/canvas.height>1.25?3:2;
    const rows=6/columns, cw=width/columns, ch=height/rows;
    const titles=["中文与英文","深色界面","子像素彩边","细线与曲线","源像素条纹","渐变与像素图"];
    for(let i=0;i<6;i++){
      const x=Math.round((i%columns)*cw), y=Math.round(Math.floor(i/columns)*ch);
      const right=Math.round((i%columns+1)*cw), bottom=Math.round((Math.floor(i/columns)+1)*ch);
      const tw=right-x, th=bottom-y, w=tw/density, h=th/density;
      ctx.save();ctx.beginPath();ctx.rect(x,y,tw,th);ctx.clip();
      ctx.fillStyle=i===1?"#24212a":i===5?"#f6f2f8":"#fff";
      ctx.fillRect(x,y,tw,th);
      ctx.translate(x,y);ctx.scale(density,density);
      const pad=Math.min(22,w*.08), textY=49;
      label(ctx,`${String(i+1).padStart(2,"0")}  ${titles[i]}`,pad,16,10,i===1?"#b9accb":"#93859d",600);
      rule(ctx,pad,36,w-pad*2,i===1?"#443b4f":"#e9e3ec");
      if(i===0){
        label(ctx,"看清每一个像素",pad,textY,Math.min(23,w/9),"#302637",600);
        const lines=[
          ["The quick brown fox 012345",14],
          ["参考屏 · 虚拟显示器 · 文字滚动",14],
          ["AaBb  Il1  O0  rn m  AVW",12],
          ["0123456789 / AaBb CcDd",12],
          ["细小笔画，标点与字间空隙。",11],
          ["Window / Resolution / Display",13],
          ["清晰度与稳定性同时比较",14]
        ];
        lines.forEach(([t,s],n)=>label(ctx,t,pad,textY+40+n*27,s));
      }else if(i===1){
        label(ctx,"Display settings",pad,textY,21,"#f3ebfb",500);
        label(ctx,"低对比度 · 白字与灰字",pad,textY+35,13,"#bcb0c7");
        for(let n=0;n<Math.floor((h-110)/40);n++){
          rule(ctx,pad,109+n*40,w-pad*2,"#3a3442");
          label(ctx,["Reference screen","Brightness   75%","3840 × 2160 / 120 Hz","窗口与虚拟显示器"][n%4],pad,120+n*40,12,n%2?"#9e93aa":"#f4edf9");
        }
      }else if(i===2){
        ctx.save();ctx.translate(pad,textY);
        rgbText(ctx,Math.max(1,w-pad*2),Math.min(83,Math.max(1,h-54)),density);
        ctx.restore();
        const sw=Math.max(3,(w-pad*2-16)/3);
        ["#e6444b","#269c73","#3278d2"].forEach((color,n)=>{
          ctx.fillStyle=color;ctx.fillRect(pad+n*(sw+8),151,sw,27);
        });
        if(h>220){
          ["#e6444b","#269c73","#3278d2"].forEach((color,n)=>{
            const xx=pad+n*(sw+8);
            label(ctx,["Red 红","Green 绿","Blue 蓝"][n],xx,191,12,color,500);
            ctx.fillStyle=color;
            ctx.fillRect(xx,Math.floor(215*density)/density,sw,1/density);
            ctx.fillRect(Math.floor((xx+sw/2)*density)/density,208,1/density,12);
          });
        }
        if(h>274){
          ctx.fillStyle="#242129";ctx.fillRect(pad,229,Math.max(0,w-pad*2),h-248);
          label(ctx,"White on dark / 白色边缘",pad+7,241,12,"#fff");
        }
      }else if(i===3){
        const body=Math.max(55,h-70);
        ctx.strokeStyle="#584270";
        for(let n=0;n<3;n++){
          ctx.lineWidth=[.5,1,2][n];
          ctx.beginPath();ctx.moveTo(pad,textY+20+n*30);ctx.lineTo(w-pad,textY+n*24);ctx.stroke();
        }
        const cy=textY+body*.7, r=Math.max(12,Math.min(w*.2,body*.25));
        ctx.lineWidth=1;
        ctx.beginPath();ctx.arc(w*.28,cy,r,0,Math.PI*2);ctx.stroke();
        ctx.beginPath();ctx.moveTo(w*.53,cy+r);ctx.bezierCurveTo(w*.8,cy-r*2,w*.59,cy+r*2,w-pad,cy-r);ctx.stroke();
        label(ctx,"0.5 / 1 / 2 DIP",pad,h-27,10,"#8c7e98");
      }else if(i===4){
        // The patterns below intentionally remain fixed in SOURCE pixels.
        ctx.restore();ctx.save();ctx.beginPath();ctx.rect(x,y,tw,th);ctx.clip();
        const px=x+Math.round(pad*density), py=y+Math.round(textY*density);
        const pw=Math.max(1,tw-Math.round(pad*2*density)), available=bottom-py-Math.round(18*density);
        const band=Math.max(1,Math.floor((available-16*density)/3));
        ctx.fillStyle=tilePattern(ctx,"stripes");ctx.fillRect(px,py,pw,band);
        ctx.fillStyle=tilePattern(ctx,"checker");ctx.fillRect(px,py+band+8*density,pw,band);
        ctx.fillStyle="#fff";ctx.fillRect(px,py+2*band+16*density,pw,band);
        ctx.save();ctx.beginPath();ctx.rect(px,py+2*band+16*density,pw,band);ctx.clip();
        ctx.strokeStyle="#151515";ctx.lineWidth=1;
        for(let n=-band;n<pw+band;n+=4){
          ctx.beginPath();ctx.moveTo(px+n,py+2*band+16*density);
          ctx.lineTo(px+n+band,py+3*band+16*density);ctx.stroke();
        }
        ctx.restore();
      }else{
        const grad=ctx.createLinearGradient(pad,textY,w-pad,textY+80);
        grad.addColorStop(0,"#504580");grad.addColorStop(.45,"#cd8a90");grad.addColorStop(1,"#eacb91");
        ctx.fillStyle=grad;ctx.fillRect(pad,textY,w-pad*2,Math.min(75,h*.28));
        ctx.strokeStyle="#fff9";ctx.lineWidth=.7;
        for(let n=0;n<12;n++){
          ctx.beginPath();ctx.moveTo(pad,textY+n*5);
          ctx.bezierCurveTo(w*.3,textY+90,w*.6,textY-40,w-pad,textY+n*5+10);ctx.stroke();
        }
        const unit=Math.max(1,Math.floor(Math.min(w/34,(h-155)/16)));
        const yy=textY+100;
        for(let b=0;b<3;b++)for(let by=0;by<9;by++)for(let bx=0;bx<7;bx++){
          if((bx-3)**2+(by-4)**2<16&&((bx+by+b)%4!==0)){
            ctx.fillStyle=["#79569b","#368570","#bd6657"][b];
            ctx.fillRect(pad+b*unit*10+bx*unit,yy+by*unit,unit,unit);
          }
        }
        label(ctx,"位图纹理 / 像素边缘",pad,h-27,10,"#8c7e98");
      }
      ctx.restore();
      ctx.fillStyle="#d9d1dd";ctx.fillRect(right-1,y,1,th);ctx.fillRect(x,bottom-1,tw,1);
    }
    return c;
  }

  function compile(type,text) {
    const shader=gl.createShader(type);
    gl.shaderSource(shader,text);gl.compileShader(shader);
    if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(shader));
    return shader;
  }
  function link(fragment) {
    const linked=gl.createProgram();
    const vertex=compile(gl.VERTEX_SHADER,F.vertexSource),pixel=compile(gl.FRAGMENT_SHADER,fragment);
    gl.attachShader(linked,vertex);gl.attachShader(linked,pixel);
    gl.linkProgram(linked);
    gl.deleteShader(vertex);gl.deleteShader(pixel);
    if(!gl.getProgramParameter(linked,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(linked));
    return linked;
  }
  function setupGL() {
    gl=canvas.getContext("webgl2",{alpha:false,antialias:false,depth:false,stencil:false,preserveDrawingBuffer:true});
    if(!gl)throw Error("需要支持 WebGL2 的浏览器");
    program=link(F.fragmentSource);
    cleanProgram=link(F.defringeFragmentSource);
    cleanUniforms=Object.fromEntries(["u_source","u_sourceSize","u_refine"].map(n=>[n,gl.getUniformLocation(cleanProgram,n)]));
    gl.useProgram(program);
    uniforms=Object.fromEntries(["u_source","u_cleanSource","u_legacySource","u_compareLegacy","u_compareSbms","u_comparePrevious","u_nativePassthrough","u_upscaleChroma","u_sourceSize","u_outputSize","u_split","u_motion","u_mode","u_ewaLut","u_viewOrigin","u_viewZoom"].map(n=>[n,gl.getUniformLocation(program,n)]));
    // Keep byte comparisons reproducible, including the source cleanup pass.
    gl.disable(gl.DITHER);
    gl.bindVertexArray(gl.createVertexArray());
    texture=gl.createTexture();
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,texture);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.REPEAT);
    gl.uniform1i(uniforms.u_source,0);
    const lut=gl.createTexture();
    gl.activeTexture(gl.TEXTURE1);gl.bindTexture(gl.TEXTURE_2D,lut);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D,0,gl.R32F,F.ewaLut.length,1,0,gl.RED,gl.FLOAT,F.ewaLut);
    gl.uniform1i(uniforms.u_ewaLut,1);
    cleanTexture=gl.createTexture();
    gl.activeTexture(gl.TEXTURE2);gl.bindTexture(gl.TEXTURE_2D,cleanTexture);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.REPEAT);
    gl.uniform1i(uniforms.u_cleanSource,2);
    gl.uniform1i(uniforms.u_legacySource,reviewMode||!testMode?3:0);
    if(reviewMode||!testMode){
      legacyTexture=gl.createTexture();
      gl.activeTexture(gl.TEXTURE3);gl.bindTexture(gl.TEXTURE_2D,legacyTexture);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.REPEAT);
    }
    cleanFramebuffer=gl.createFramebuffer();
  }
  function prepareCleanSource(w,h,target=cleanTexture,refine=0) {
    gl.activeTexture(gl.TEXTURE2);gl.bindTexture(gl.TEXTURE_2D,target);
    gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8,w,h,0,gl.RGBA,gl.UNSIGNED_BYTE,null);
    gl.bindFramebuffer(gl.FRAMEBUFFER,cleanFramebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,target,0);
    if(gl.checkFramebufferStatus(gl.FRAMEBUFFER)!==gl.FRAMEBUFFER_COMPLETE)throw Error("去彩边缓冲初始化失败");
    gl.useProgram(cleanProgram);
    gl.uniform1i(cleanUniforms.u_source,0);
    gl.uniform1i(cleanUniforms.u_refine,refine);
    gl.uniform2f(cleanUniforms.u_sourceSize,w,h);
    gl.viewport(0,0,w,h);
    gl.drawArrays(gl.TRIANGLES,0,3);
    gl.bindFramebuffer(gl.FRAMEBUFFER,null);
    gl.useProgram(program);
  }
  function fitPreview() {
    const r=$("stage").getBoundingClientRect(), dpr=window.devicePixelRatio||1;
    const scale=Math.min(r.width/canvas.width,r.height/canvas.height);
    $("comparison").style.width=`${canvas.width*scale}px`;
    $("comparison").style.height=`${canvas.height*scale}px`;
    dimensions.dpr=dpr;
    dimensions.previewScale=scale*dpr;
    $("status").textContent=`预览 ${Math.round(dimensions.previewScale*100)}%${state.focus?` · 特写 ${view().zoom}×`:""}`;
    $("status").title="窗口适配倍率；采样分辨率固定，预览采用像素化显示";
  }
  function rebuild() {
    const s=scenarios[state.scenario];
    [canvas.width,canvas.height]=s.target;
    const w=Math.max(1,Math.round(canvas.width/s.ratio[0])), h=Math.max(1,Math.round(canvas.height/s.ratio[1]));
    if(Math.max(w,h)>gl.getParameter(gl.MAX_TEXTURE_SIZE))throw Error("当前 GPU 不支持此场景的源纹理尺寸");
    const asset=activeAsset();
    const dpi=asset?.kind==="capture"?asset.dpi/100
      :state.dpi==="auto"?Math.max(1,1/s.ratio[0]):Number(state.dpi)/100;
    source=drawSource(w,h,dpi);
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,false);
    gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL,gl.NONE);
    gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,source);
    if(reviewMode||!testMode)prepareCleanSource(w,h,legacyTexture,reviewMode?0:1);
    prepareCleanSource(w,h,cleanTexture,!testMode?2:reviewMode?1:0);
    gl.viewport(0,0,canvas.width,canvas.height);
    gl.uniform2f(uniforms.u_sourceSize,w,h);
    gl.uniform2f(uniforms.u_outputSize,canvas.width,canvas.height);
    const sx=w/canvas.width, sy=h/canvas.height;
    const area=sx>1.0001&&sx<=2&&sy>1.0001&&sy<=2;
    const native=Math.abs(sx-1)<.0001&&Math.abs(sy-1)<.0001;
    const up=sx<=1&&sy<=1&&!native;
    dimensions={width:canvas.width,height:canvas.height,sourceWidth:w,sourceHeight:h,dpi:dpi*100,area,native,up};
    const kernel=native?"1:1":up?"EWA Lanczos4":"Lanczos3";
    $("left-tag").textContent=`新方案 · ${kernel}`;
    $("left-tag").title=up?"EWA Lanczos4Sharpest · 0.8 抗振铃":native?"原像素":"按缩小率扩展核的 Lanczos3";
    $("right-tag").textContent="新方案＋自适应去彩边";
    $("right-tag").title=`相同 ${kernel}；额外处理源图可疑 RGB 彩边，保留亮度`;
    if(reviewMode){
      $("left-tag").textContent=state.reference==="legacy"?"旧去彩边":"未去彩边";
      $("right-tag").textContent="改进去彩边";
      $("left-tag").title=$("right-tag").title=`相同 ${kernel} 缩放核`;
    }
    if(!testMode)updateComparison();
    $("branch").textContent=`源 DPI ${Number((dpi*100).toFixed(1))}% · ${w}×${h} → ${canvas.width}×${canvas.height}`;
    if(realMode||(reviewMode&&state.sample!=="positive"))$("branch").textContent=`${reviewMode&&state.sample==="both"?"RGB 文字 / TikTok":"TikTok 官方图标"} · 源图标 ${state.logoSize}×${state.logoSize} px · 输出 ${Number((canvas.width/w).toFixed(2))}×${sx===sy?"":` / ${Number((canvas.height/h).toFixed(2))}×`}`;
    if(!testMode&&state.content!=="positive"){
      const family=corpusAssets.find(a=>a.id===state.content);
      const em=asset.kind==="capture"?Math.abs(asset.regions[0].fontHeightPx)*canvas.height/h:0;
      $("branch").textContent=family.variants
        ?`源 DPI ${Number((dpi*100).toFixed(1))}% · 输出字号 ${Number(em.toFixed(2))} px${state.scenario==="x75"?" · 横向压缩":""} · ${w}×${h} → ${canvas.width}×${canvas.height}`
        :asset.kind==="capture"
        ?`${asset.name} · 固定截图 · ${w}×${h} → ${canvas.width}×${canvas.height}`
        :`素材长边 ${state.assetSize} 源像素 · ${w}×${h} → ${canvas.width}×${canvas.height}`;
    }
    $("branch").title=$("branch").textContent;
    fitPreview();
    render();
    document.body.dataset.ready="true";
  }
  function motion() {
    const sx=source.width/canvas.width,sy=source.height/canvas.height;
    return [state.motion==="y"?0:Math.floor(state.time*state.speed*sx),
      state.motion==="x"?0:Math.floor(state.time*state.speed*sy)];
  }
  function view() {
    if(!state.focus)return {origin:[0,0],zoom:1};
    if(!testMode&&state.content!=="positive"){
      const r=corpusRegions.reduce((best,r)=>Math.hypot(r.x+r.width/2-source.width/2,r.y+r.height/2-source.height/2)<Math.hypot(best.x+best.width/2-source.width/2,best.y+best.height/2-source.height/2)?r:best);
      const asset=activeAsset();
      const focal=asset.kind==="capture"?asset.focus:{x:r.width/2,y:r.height/2};
      return {zoom:8,origin:[
        Math.floor((r.x+focal.x)*canvas.width/source.width-canvas.width/16),
        Math.floor((r.y+focal.y)*canvas.height/source.height-canvas.height/16)
      ]};
    }
    if(realMode||(reviewMode&&state.sample!=="positive"))return {zoom:reviewMode&&state.sample==="both"?4:8,origin:[
      Math.floor(canvas.width/2-canvas.width/(reviewMode&&state.sample==="both"?8:16)),
      Math.floor(canvas.height/2-canvas.height/(reviewMode&&state.sample==="both"?8:16))
    ]};
    if(attackMode){
      const r=attackRegions[0];
      return {zoom:8,origin:[
        Math.floor((r.x+r.width/2)*canvas.width/source.width-canvas.width/16),
        Math.floor(r.y*canvas.height/source.height-12)
      ]};
    }
    const density=dimensions.dpi/100;
    const pad=Math.min(22,source.width/3/density*.08);
    return {zoom:8,origin:[
      Math.floor(canvas.width*2/3+pad*density*canvas.width/source.width-16),
      Math.floor(49*density*canvas.height/source.height-16)
    ]};
  }
  function render() {
    const m=motion(),v=view();
    gl.uniform1f(uniforms.u_split,state.split);
    gl.uniform2f(uniforms.u_motion,...m);
    gl.uniform2f(uniforms.u_viewOrigin,...v.origin);
    gl.uniform1f(uniforms.u_viewZoom,v.zoom);
    gl.uniform1i(uniforms.u_mode,renderMode);
    gl.uniform1i(uniforms.u_compareLegacy,(!testMode||reviewMode)&&state.reference==="legacy"?1:0);
    gl.uniform1i(uniforms.u_compareSbms,!testMode&&state.reference==="sbms"?1:0);
    gl.uniform1i(uniforms.u_comparePrevious,!testMode&&state.reference==="previous"?1:0);
    gl.uniform1i(uniforms.u_nativePassthrough,0);
    gl.uniform1i(uniforms.u_upscaleChroma,!testMode?1:0);
    gl.drawArrays(gl.TRIANGLES,0,3);
    frameCount++;
  }
  function tick(now) {
    // Display scaling changes presentation only, never the sampling buffers.
    if(dimensions && dimensions.dpr!==(window.devicePixelRatio||1))fitPreview();
    if(lastTime!==undefined && state.playing && !document.hidden){
      state.time+=Math.min((now-lastTime)/1000,.25);
      render();frames++;
    }
    lastTime=now;
    if(now-fpsTime>750){
      $("metrics").textContent=state.playing?`${Math.round(frames*1000/(now-fpsTime))} fps · 预览`:"已暂停";
      fpsTime=now;frames=0;
    }
    raf=requestAnimationFrame(tick);
  }
  function split(value) {
    state.split=Math.max(0,Math.min(1,value));
    $("stage").style.setProperty("--split",`${state.split*100}%`);
    $("divider").setAttribute("aria-valuenow",String(Math.round(state.split*100)));
    if(gl&&source)render();
  }
  function play(value) {
    state.playing=value;
    $("play").setAttribute("aria-label",value?"暂停":"播放");
    $("play").title=value?"暂停 · Space":"播放 · Space";
    $("play").innerHTML=value?'<svg viewBox="0 0 24 24"><path d="M8 5v14M16 5v14"/></svg>':'<svg viewBox="0 0 24 24"><path d="m8 5 11 7-11 7Z"/></svg>';
    $("metrics").textContent=value?"播放中":"已暂停";
  }
  function step() { play(false);state.time+=1/60;render(); }
  function updateComparison() {
    const legacy=state.reference==="legacy";
    const sbms=state.reference==="sbms";
    const previous=state.reference==="previous";
    $("dynamic-reference").value=state.reference;
    $("left-tag").textContent=previous?"优化前":sbms?"SBMS 当前":legacy?"修改前":"未去彩边";
    $("right-tag").textContent=previous?"优化后":legacy?"修改后":"优化版";
    $("left-tag").title=previous?"上一版完整方案：最终去彩边＋RGB EWA / Lanczos3":sbms?"当前 SBMS 采样：双轴缩小且每轴不超过 2 倍时使用面积平均＋原有去彩边，其余双线性":legacy?"上一版去彩边；相同缩放核":"原始源像素，不做去彩边；相同缩放核";
    $("right-tag").title="1:1 原像素；放大保留 EWA 明暗细节，色度使用双线性；缩小使用 Lanczos3";
    $("stage").setAttribute("aria-label",previous?"左侧优化前，右侧优化后；相同去彩边，仅改变放大色度采样":sbms?"左侧当前 SBMS 采样，右侧优化方案；相同源像素":legacy
      ?"左侧修改前的去彩边，右侧修改后；相同源像素与缩放核"
      :"左侧未去彩边，右侧最终版去彩边；相同源像素与缩放核");
  }
  function setState(next) {
    if(!testMode&&next.content&&next.content!=="positive"){
      const asset=corpusAssets.find(a=>a.id===next.content);
      if(!asset.sizes.includes(next.assetSize??state.assetSize))next.assetSize=asset.kind==="capture"?asset.sizes[0]:asset.kind==="photo"?512:48;
    }
    const rebuildNeeded=["scenario","dpi","logoSize","sample","content","assetSize","background"].some(k=>k in next&&next[k]!==state[k]);
    Object.assign(state,next);
    for(const k of ["scenario","motion","speed","dpi"])$(k).value=String(state[k]);
    if(realMode||reviewMode)$("logo-size").value=String(state.logoSize);
    if(reviewMode){
      $("sample").value=state.sample;$("reference").value=state.reference;
      $("left-tag").textContent=state.reference==="legacy"?"旧去彩边":"未去彩边";
      $("logo-size").hidden=state.sample==="positive";
      $("focus").title=`当前场景 · 暂停 · ${state.sample==="both"?4:8} 倍像素特写`;
    }
    $("focus").textContent=state.focus?"返回全图":reviewMode&&state.sample==="both"?"细节 ×4":realMode||(reviewMode&&state.sample==="logo")?"图标 ×8":"彩边 ×8";
    if(!testMode){
      updateComparison();
      $("content").value=state.content;$("background").value=state.background;
      const selectedAsset=corpusAssets.find(a=>a.id===state.content);
      $("asset-settings").hidden=state.content==="positive"||selectedAsset.kind==="capture";
      $("dpi").hidden=state.content!=="positive";document.querySelector('label[for="dpi"]').hidden=state.content!=="positive";
      if(state.content!=="positive"){
        const asset=corpusAssets.find(a=>a.id===state.content);
        $("asset-size").replaceChildren(...asset.sizes.map(size=>new Option(`${size} 源像素`,String(size))));
        $("asset-size").value=String(state.assetSize);$("background").disabled=asset.kind!=="icon";
        $("focus").textContent=state.focus?"返回全图":"细节 ×8";
      }
    }
    $("focus").setAttribute("aria-pressed",String(state.focus));
    play(state.playing);
    // Render only after the source and its focus region match the new state.
    if(rebuildNeeded)rebuild();
    split(state.split);fitPreview();
    return {...state};
  }
  function exportFrame(mode=0) {
    renderMode=mode;render();gl.finish();
    const url=canvas.toDataURL("image/png");
    renderMode=0;render();
    return url;
  }
  function pixels(mode=0) {
    renderMode=mode;render();gl.finish();
    const raw=new Uint8Array(canvas.width*canvas.height*4);
    gl.readPixels(0,0,canvas.width,canvas.height,gl.RGBA,gl.UNSIGNED_BYTE,raw);
    renderMode=0;render();
    return raw;
  }
  function cleanPixels(legacy=false) {
    const raw=new Uint8Array(source.width*source.height*4);
    gl.bindFramebuffer(gl.FRAMEBUFFER,cleanFramebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,legacy?legacyTexture:cleanTexture,0);
    gl.readPixels(0,0,source.width,source.height,gl.RGBA,gl.UNSIGNED_BYTE,raw);
    gl.bindFramebuffer(gl.FRAMEBUFFER,null);
    return raw;
  }
  try{
    if(!testMode){
      $("content").append(...corpusAssets.map(a=>new Option(
        a.kind==="capture"&&!a.variants?`固定 ${a.dpi}% · ${a.id.endsWith("-grayscale")?"灰度":"ClearType"}`:a.name,a.id)));
      $("content").addEventListener("change",()=>setState({content:$("content").value,time:0}));
      $("asset-size").addEventListener("change",()=>setState({assetSize:Number($("asset-size").value),time:0}));
      $("background").addEventListener("change",()=>setState({background:$("background").value,time:0}));
      $("dynamic-reference").addEventListener("change",()=>setState({reference:$("dynamic-reference").value}));
      $("dynamic-motion").append($("motion"),$("speed"));
      $("info").innerHTML='优化前后使用相同去彩边；放大时只调整色度采样，保留 EWA 明暗细节。可切换到 SBMS 当前方案对照。<span>“Windows 文字 / 灰度”按比例选择独立原生捕获，保持输出约 12 px 字号。固定截图保留原 DPI。静态预处理已缓存，预览 FPS 不含逐帧去彩边成本。</span><span><a href="assets/text-capture/adaptive/README.md">文字来源</a> · <a href="corpus.html">图标复测</a></span>';
    }
    if(realMode||reviewMode){
      document.querySelector(".brand").textContent=reviewMode?"去彩边对比":"TikTok";
      $("focus").title="同一官方图标 · 8 倍像素特写";
      $("logo-size").addEventListener("change",()=>setState({logoSize:Number($("logo-size").value),time:0}));
    }
    if(reviewMode){
      $("sample").addEventListener("change",()=>setState({sample:$("sample").value,time:0}));
      $("reference").addEventListener("change",()=>setState({reference:$("reference").value}));
    }
    if(attackMode){
      document.querySelector(".brand").textContent="真彩色误判";
      $("focus").title="真实红线 · 当前场景 · 8 倍像素特写";
      $("info").textContent="红线是真正的 RGB 255,0,0。黑白交界的 1 px 红线、4 px 红线，对照白底与横向红线。开场 1:1，无缩放插值；右侧使用原有去彩边算法，参数未改。";
    }
    setupGL();
    ["scenario","dpi"].forEach(k=>$(k).addEventListener("change",()=>setState({[k]:$(k).value,time:0})));
    $("motion").addEventListener("change",()=>setState({motion:$("motion").value,time:0}));
    $("speed").addEventListener("change",()=>setState({speed:Number($("speed").value)}));
    $("play").addEventListener("click",()=>play(!state.playing));
    $("step").addEventListener("click",step);
    $("focus").addEventListener("click",()=>setState(state.focus
      ?{focus:false}
      :{focus:true,playing:false,time:0,motion:"x",split:.5}));
    $("reset").addEventListener("click",()=>setState({time:0,split:.5}));
    $("settings").addEventListener("click",()=>{
      $("settings-panel").hidden=!$("settings-panel").hidden;
      $("settings").setAttribute("aria-expanded",String(!$("settings-panel").hidden));
    });
    $("fullscreen").addEventListener("click",async()=>{
      if(document.fullscreenElement)await document.exitFullscreen();
      else await document.documentElement.requestFullscreen();
    });
    $("divider").addEventListener("pointerdown",e=>{
      $("divider").setPointerCapture(e.pointerId);
      $("divider").dataset.dragging="true";
    });
    $("divider").addEventListener("pointermove",e=>{
      if($("divider").hasPointerCapture(e.pointerId)){
        const r=$("comparison").getBoundingClientRect();
        split((e.clientX-r.left)/r.width);
      }
    });
    $("divider").addEventListener("pointerup",e=>{
      $("divider").releasePointerCapture(e.pointerId);
      delete $("divider").dataset.dragging;
    });
    $("divider").addEventListener("keydown",e=>{
      if(e.key==="ArrowLeft"||e.key==="ArrowRight"){
        e.preventDefault();split(state.split+(e.key==="ArrowLeft"?-.02:.02));
      }
      if(e.key==="Home"){e.preventDefault();split(0);}
      if(e.key==="End"){e.preventDefault();split(1);}
    });
    window.addEventListener("keydown",e=>{
      if(["INPUT","SELECT","BUTTON"].includes(e.target.tagName))return;
      if(e.code==="Space"){e.preventDefault();play(!state.playing);}
      if(e.code==="Period"){e.preventDefault();step();}
      if(e.key==="Escape"){$("settings-panel").hidden=true;$("settings").setAttribute("aria-expanded","false");}
    });
    document.addEventListener("pointerdown",e=>{
      if(!$("settings-panel").contains(e.target)&&!$("settings").contains(e.target)){
        $("settings-panel").hidden=true;$("settings").setAttribute("aria-expanded","false");
      }
    });
    new ResizeObserver(()=>{
      clearTimeout(resizeTimer);
      resizeTimer=setTimeout(()=>{try{if(dimensions)fitPreview();}catch(e){fail(e);}},100);
    }).observe($("stage"));
    window.dynamicLab={
      state,scenarios,errors,setState,step,exportFrame,pixels,cleanPixels,
      get dimensions(){return dimensions;},
      get source(){return source;},
      get motion(){return motion();},
      get view(){return view();},
      get frameCount(){return frameCount;},
      get gl(){return gl;},
      get filter(){return F;},
      get attack(){return {enabled:attackMode,regions:attackRegions};},
      get realLogo(){return {enabled:realMode||(reviewMode&&state.sample==="logo"),region:logoRegion};},
      get review(){return {enabled:reviewMode,regions:reviewRegions};},
      get corpus(){return {assets:corpusAssets,regions:corpusRegions,activeAsset:activeAsset()};}
    };
    const start=()=>{rebuild();setState({});raf=requestAnimationFrame(tick);};
    if(realMode||reviewMode)$("real-logo").decode().then(start).catch(fail);
    else if(!testMode)Promise.all(corpusAssets.flatMap(a=>a.variants||[a]).map(async asset=>{
      const img=new Image();img.src=asset.dataUrl;await img.decode();assetImages.set(asset.id,img);
    })).then(start).catch(fail);
    else start();
  }catch(e){fail(e);}
})();
