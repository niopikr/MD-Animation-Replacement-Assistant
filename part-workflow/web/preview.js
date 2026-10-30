import { readSkeletonJson } from './rigby/src/core/interop.js';
import { Pose } from './rigby/src/core/pose.js';
import { parseAtlas } from './rigby/src/core/atlas.js';
import { WebGLRenderer } from './rigby/src/webgl/renderer.js';

// MIT-licensed clean-room runtime; no official Spine runtime or external viewer is loaded.
let disposeCurrent;
window.openAnimationPreview = async function(id, token) {
  disposeCurrent?.();
  const dialog=document.getElementById('animationDialog'), canvas=document.getElementById('animationCanvas');
  const status=document.getElementById('animationStatus'), play=document.getElementById('animationPlay');
  async function read(url,json=true){const r=await fetch(url,{headers:{'X-Workflow-Token':token}});if(!r.ok)throw Error('预览素材读取失败 '+r.status);return json?r.json():r.text();}
  const info=await read(`/api/projects/${id}/preview-model`);
  const [json,atlasText]=await Promise.all([read(info.skeletonUrl),read(info.atlasUrl,false)]);
  const {skeleton:sk,warnings}=readSkeletonJson(json);
  const images=new Map(await Promise.all(info.pages.map(async p=>{const img=new Image();await new Promise((resolve,reject)=>{img.onload=resolve;img.onerror=()=>reject(Error('贴图页读取失败 '+p.name));img.src=p.url;});return[p.name,img];})));
  const gl=canvas.getContext('webgl',{alpha:true,premultipliedAlpha:false});if(!gl)throw Error('浏览器未提供 WebGL，请在支持 WebGL 的浏览器打开。');
  const renderer=new WebGLRenderer(gl), textures=[];
  const create=renderer.createTexture.bind(renderer);renderer.createTexture=(...args)=>{const t=create(...args);textures.push(t.texture);return t;};
  const atlas=parseAtlas(atlasText), lookup=renderer.atlasTextures(atlas,images), pose=new Pose(sk);
  const select=document.getElementById('animationSelect');select.replaceChildren(...sk.animations.map(a=>new Option(a.name,a.name)));
  if(!sk.animations.length)throw Error('骨骼没有动作');
  const scrub=document.getElementById('animationTime'),speed=document.getElementById('animationSpeed'),loop=document.getElementById('animationLoop');
  let panX=0,panY=0,panDrag=null;let zoomFactor=1;const zoomControl=document.getElementById('animationZoom');
  function zoom(value){zoomFactor=Math.max(.25,Math.min(4,value));zoomControl.value=Math.round(zoomFactor*100);document.getElementById('animationZoomText').textContent=Math.round(zoomFactor*100)+'%';}
  zoom(1);zoomControl.oninput=()=>zoom(+zoomControl.value/100);document.getElementById('animationFit').onclick=()=>{panX=0;panY=0;zoom(1);};canvas.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();canvas.setPointerCapture(e.pointerId);panDrag={x:e.clientX,y:e.clientY,px:panX,py:panY};};canvas.onpointermove=e=>{if(!panDrag)return;panX=panDrag.px+e.clientX-panDrag.x;panY=panDrag.py+e.clientY-panDrag.y;};canvas.onpointerup=canvas.onpointercancel=()=>panDrag=null;canvas.onwheel=e=>{e.preventDefault();zoom(zoomFactor*(e.deltaY<0?1.1:1/1.1));};
  let animation=sk.animations[0],time=0,playing=true,last=performance.now(),frame=0,raf,stopped=false,bounds;
  function sample(t){pose.reset(sk);pose.apply(sk,animation,t,1);pose.updateWorld(sk);}
  function fit(){let x0=Infinity,y0=Infinity,x1=-Infinity,y1=-Infinity;
    for(let n=0;n<=24;n++){sample(animation.duration*n/24);for(const quad of pose.quads(sk))for(const p of quad.positions){if(!p.every(Number.isFinite))continue;x0=Math.min(x0,p[0]);y0=Math.min(y0,p[1]);x1=Math.max(x1,p[0]);y1=Math.max(y1,p[1]);}}
    bounds=Number.isFinite(x0)?{x:(x0+x1)/2,y:(y0+y1)/2,w:Math.max(1,x1-x0),h:Math.max(1,y1-y0)}:{x:0,y:0,w:3000,h:3000};sample(time);
  }
  function draw(now){if(stopped)return;const dt=Math.min(.1,(now-last)/1000);last=now;
    if(playing){time+=dt*+speed.value;if(time>animation.duration){if(loop.checked)time%=Math.max(.001,animation.duration);else{time=animation.duration;playing=false;play.textContent='播放';}}}
    sample(time);const rect=canvas.getBoundingClientRect(),dpr=devicePixelRatio||1;const w=Math.max(1,Math.round(rect.width*dpr)),h=Math.max(1,Math.round(rect.height*dpr));if(canvas.width!==w)canvas.width=w;if(canvas.height!==h)canvas.height=h;
    gl.viewport(0,0,w,h);gl.clearColor(.09,.11,.15,1);gl.clear(gl.COLOR_BUFFER_BIT);
    const zoomScale=zoomFactor*.85*Math.min(w/bounds.w,h/bounds.h);canvas.dataset.panX=panX;canvas.dataset.panY=panY;renderer.begin(w,h,{x:bounds.x-panX*dpr/zoomScale,y:bounds.y+panY*dpr/zoomScale,zoom:zoomScale});renderer.drawSkeleton(sk,pose,lookup);renderer.end();
    scrub.value=time;document.getElementById('animationClock').textContent=`${time.toFixed(2)} / ${animation.duration.toFixed(2)} 秒 · 帧 ${++frame}`;
    raf=requestAnimationFrame(draw);
  }
  play.onclick=()=>{playing=!playing;play.textContent=playing?'暂停':'播放';};scrub.oninput=()=>{time=+scrub.value;playing=false;play.textContent='播放';};
  select.onchange=()=>{animation=sk.animations.find(a=>a.name===select.value);time=0;scrub.max=animation.duration;fit();};
  fit();scrub.max=animation.duration;play.textContent='暂停';
  status.textContent=`独立实验预览 · ${info.previewVariant||""} · Rigby2D · ${sk.bones.length} 骨骼 / ${sk.slots.length} 插槽。${warnings.length?'兼容警告：'+warnings.join('；'):'解析未报告不支持项；画面仍需人工核对。'}`;
  document.getElementById('closeAnimationButton').onclick=()=>dialog.close();
  disposeCurrent=()=>{canvas.onwheel=null;canvas.onpointerdown=canvas.onpointermove=canvas.onpointerup=canvas.onpointercancel=null;stopped=true;cancelAnimationFrame(raf);for(const t of textures)gl.deleteTexture(t);gl.deleteBuffer(renderer.vertexBuffer);gl.deleteBuffer(renderer.indexBuffer);gl.deleteProgram(renderer.program);disposeCurrent=null;};
  dialog.onclose=()=>disposeCurrent?.();dialog.showModal();raf=requestAnimationFrame(draw);
};
// Tail layer is rendered separately and never enters the selection mask.
let referenceScenePromise,referenceProject,referenceGeneration=0,selectedReferencePart;
let referenceView={zoom:1,x:0,y:0};
async function loadFrameScene(id,token,original=true,revision=''){
 async function read(url,json=true){const r=await fetch(url,{headers:{'X-Workflow-Token':token}});if(!r.ok)throw Error('动画素材读取失败 '+r.status);return json?r.json():r.text();}
 const info=await read(`/api/projects/${id}/preview-model?original=${original}${revision?'&revision='+encodeURIComponent(revision):''}`);
 const [json,text]=await Promise.all([read(info.skeletonUrl),read(info.atlasUrl,false)]),{skeleton:sk}=readSkeletonJson(json),pose=new Pose(sk);
 const images=new Map(await Promise.all(info.pages.map(async p=>{const img=new Image();await new Promise((ok,no)=>{img.onload=ok;img.onerror=()=>no(Error('贴图读取失败'));img.src=p.url;});return[p.name,img];})));
 const canvas=document.createElement('canvas');canvas.width=1024;canvas.height=768;
 const gl=canvas.getContext('webgl',{alpha:true,premultipliedAlpha:true});if(!gl)throw Error('尾帧图层需要WebGL');
 const renderer=new WebGLRenderer(gl),lookup=renderer.atlasTextures(parseAtlas(text),images);
 let animation=sk.animations[0];if(!animation)throw Error('模型没有动作');
 const sample=t=>{pose.reset(sk);pose.apply(sk,animation,t,1);pose.updateWorld(sk);};
 function bounds(){let x0=Infinity,y0=Infinity,x1=-Infinity,y1=-Infinity;for(let n=0;n<=24;n++){sample(animation.duration*n/24);for(const q of pose.quads(sk))for(const [x,y] of q.positions){if(!Number.isFinite(x+y))continue;x0=Math.min(x0,x);x1=Math.max(x1,x);y0=Math.min(y0,y);y1=Math.max(y1,y);}}return Number.isFinite(x0)?{x:(x0+x1)/2,y:(y0+y1)/2,zoom:.85*Math.min(1024/Math.max(1,x1-x0),768/Math.max(1,y1-y0))}:{x:0,y:0,zoom:1};}
 let camera=bounds();
 return {sk,dispose(){gl.getExtension('WEBGL_lose_context')?.loseContext();},select(name){animation=sk.animations.find(a=>a.name===name)||sk.animations[0];camera=bounds();},get duration(){return animation.duration;},draw(target,part=null,view={zoom:1,x:0,y:0},highlight=false){
  const cam={x:camera.x-view.x/(camera.zoom*view.zoom),y:camera.y+view.y/(camera.zoom*view.zoom),zoom:camera.zoom*view.zoom};
  sample(animation.duration);for(const color of pose.color)color[3]=1;
  gl.viewport(0,0,1024,768);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);renderer.begin(1024,768,cam);renderer.drawSkeleton(sk,pose,lookup);renderer.end();
  target.width=1024;target.height=768;const c=target.getContext('2d');c.drawImage(canvas,0,0);
  const quads=part?pose.quads(sk).filter(q=>sk.slots[q.slot]?.name===part.slot&&q.attachment.name===part.name):[];
  c.fillStyle='rgba(255,32,56,.6)';c.strokeStyle='#fff267';c.lineWidth=1.5;
  if(highlight)for(const q of quads){const indices=q.kind==='mesh'?q.triangles:[0,1,2,0,2,3];c.beginPath();for(let i=0;i<indices.length;i+=3){for(let n=0;n<3;n++){const p=q.positions[indices[i+n]];if(!p)continue;const x=512+(p[0]-cam.x)*cam.zoom,y=384-(p[1]-cam.y)*cam.zoom;n?c.lineTo(x,y):c.moveTo(x,y);}c.closePath();}c.fill();c.stroke();}
  target.dataset.zoom=view.zoom;target.dataset.panX=view.x;target.dataset.panY=view.y;target.dataset.highlighted=quads.length;target.dataset.part=part?.name||'';target.dataset.time=animation.duration;target.dataset.ready='true';return quads.length;
 }};
}
window.updatePartReferences=async function(id,token,part){
 const generation=++referenceGeneration;selectedReferencePart={id,token,part};const status=document.getElementById('frameReferenceStatus'),target=document.getElementById('tailFrameReference');if(!status||!target)return;
 status.textContent='正在生成原动画尾帧…';target.dataset.ready='false';window.dispatchEvent(new Event('tail-layer-updated'));
 try{
  if(referenceProject!==id){referenceView={zoom:1,x:0,y:0};document.getElementById('referenceZoom').value=100;referenceScenePromise?.then(s=>s.dispose()).catch(()=>{});referenceProject=id;referenceScenePromise=loadFrameScene(id,token);}
  const scene=await referenceScenePromise;if(generation!==referenceGeneration||!document.getElementById('tailFrameReference'))return;
  const select=document.getElementById('referenceAnimation');if(select.dataset.project!==id){select.replaceChildren(...scene.sk.animations.map(a=>new Option(a.name,a.name)));select.dataset.project=id;select.onchange=()=>{scene.select(select.value);redrawReferences();};}
  const count=scene.draw(target,part,referenceView,document.getElementById('tailLayerHighlight').checked);
  status.textContent=`尾帧 ${scene.duration.toFixed(3)}秒 · 当前部件：${part.label}（${part.name}）。${count?'':'尾帧未挂载此附件。'}`;
  window.dispatchEvent(new Event('tail-layer-updated'));
 }catch(e){if(generation!==referenceGeneration)return;referenceScenePromise=null;referenceProject=null;target.dataset.ready='false';status.textContent='尾帧不可用：'+e.message;window.dispatchEvent(new Event('tail-layer-updated'));}
};
function redrawReferences(){if(selectedReferencePart){const p=selectedReferencePart;window.updatePartReferences(p.id,p.token,p.part);}}
window.panTailReference=(x,y)=>{referenceView.x+=x;referenceView.y+=y;redrawReferences();};
window.zoomTailReference=factor=>{referenceView.zoom=Math.max(.25,Math.min(4,referenceView.zoom*factor));document.getElementById('referenceZoom').value=Math.round(referenceView.zoom*100);redrawReferences();};
document.getElementById('referenceZoom').oninput=e=>{const n=+e.target.value;if(n>=25&&n<=400){referenceView.zoom=n/100;redrawReferences();}};
document.getElementById('referenceFit').onclick=()=>{referenceView={zoom:1,x:0,y:0};document.getElementById('referenceZoom').value=100;redrawReferences();};
document.getElementById('tailLayerHighlight').onchange=redrawReferences;
window.renderExportTailFrame=async function(id,token,revision){const scene=await loadFrameScene(id,token,false,revision);try{scene.select(document.getElementById('referenceAnimation').value);const target=document.createElement('canvas');scene.draw(target);return target.toDataURL('image/png');}finally{scene.dispose();}};
window.dispatchEvent(new Event('references-ready'));
