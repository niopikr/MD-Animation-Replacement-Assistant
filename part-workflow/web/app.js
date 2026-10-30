'use strict';
const $ = id => document.getElementById(id);
const token = location.hash.slice(1) || sessionStorage.getItem('workflowToken');
if (token) sessionStorage.setItem('workflowToken', token);
let bootstrap, project, part, source, reference, mesh, cutout;
let busy = false, mode = 'manual', mask = document.createElement('canvas'), undo = [];
let view = {scale:1,x:0,y:0}, pointer = null, polygon = [], space = false, latestExport;
let dirtyMask = false, placementTimer, draftQueue = Promise.resolve(), loadGeneration = 0;
const canvas = $('sourceCanvas'), ctx = canvas.getContext('2d');
const aligned = document.createElement('canvas'), masked = document.createElement('canvas');
const tint = document.createElement('canvas'); let tintDirty = true;
const hint=document.createElement('canvas'), selectedView=document.createElement('canvas');let strokeOperation='add';
function message(text, error=false){$('notice').textContent=text;$('notice').classList.toggle('error',error);}
function saveText(text){$('saveState').textContent=text;}
async function api(path, body, raw=false){
  const response=await fetch(path,{method:body===undefined?'GET':'POST',headers:{'X-Workflow-Token':token,...(body!==undefined&&!raw?{'Content-Type':'application/json'}:{})},body:body===undefined?undefined:raw?body:JSON.stringify(body)});
  if(!response.ok){let error;try{error=(await response.json()).error}catch{error=await response.text()}throw Error(error||`请求失败 ${response.status}`);}
  if(response.headers.get('content-type')?.includes('image/')) return response.blob();
  return response.headers.get('content-type')?.includes('json')?response.json():null;
}
async function action(fn){if(busy)return;busy=true;document.body.classList.add('busy');setControls();try{await fn()}catch(e){message(e.message,true)}finally{busy=false;setControls()}}
function image(url){return new Promise((resolve,reject)=>{const i=new Image();i.onload=()=>resolve(i);i.onerror=()=>reject(Error('无法读取图片：'+url));i.src=url;})}
function fileUrl(relative,stamp=true){return `/files/${project.id}/${relative.split(/[\\/]/).map(encodeURIComponent).join('/')}?t=${token}${stamp?'&v='+Date.now():''}`;}
function partApi(suffix, p=part){return `/api/projects/${project.id}/parts/${p.number}/${suffix}`;}
function setControls(){
  if(!$('sourceCanvas'))return;
  for(const id of ['createButton','keepPendingButton','artworkInput','previewButton','exportButton','keepButton','restoreButton','generateButton','refineButton','confirmButton','previousButton','nextButton','homeButton','folderButton','undoButton','clearButton','samModeButton','lassoButton','pointButton','manualBrushButton','shapeButton','emptyButton','sourceZoomIn','sourceZoomOut','cutoutZoomIn','cutoutZoomOut','cutoutActualSize']) $(id).disabled=busy;
  $('keepPendingButton').disabled=busy||!project||!project.parts.some(p=>p.status==='pending');
  $('previousButton').disabled=busy||!project||project.currentPart<=0;
  $('nextButton').disabled=busy||!project||project.currentPart>=project.parts.length-1;
  $('generateButton').disabled=busy||!source;
  $('refineButton').disabled=busy||!source||!bootstrap?.samAvailable;
  $('samModeButton').disabled=busy||!source||!bootstrap?.samAvailable;
  $('pointButton').disabled=busy||!source||!bootstrap?.samAvailable;
  $('shapeButton').disabled=busy||!source;for(const input of $('shapeControls').querySelectorAll('input,button'))input.disabled=busy||!source;
  $('confirmButton').disabled=busy||!cutout||dirtyMask;
  for(const id of ['cutoutZoomIn','cutoutZoomOut','cutoutActualSize','cutoutPercent'])$(id).disabled=busy||!cutout;
  $('restoreButton').disabled=busy||!part?.revisions.length;
  $('exportButton').disabled=busy||!project||project.parts.some(p=>p.status==='pending');
  for(const input of document.querySelectorAll('.placement input'))input.disabled=busy||!cutout;
}
function populateCards(){
  const filter=$('cardSearch').value.trim().toLowerCase();
  const current=$('cardSelect').value;
  $('cardSelect').replaceChildren();
  for(const c of bootstrap.cards.filter(c=>`${c.name} ${c.id}`.toLowerCase().includes(filter))){const o=new Option(`${c.name}〔${c.id}〕`,c.id);$('cardSelect').append(o);}
  if([...$('cardSelect').options].some(o=>o.value===current))$('cardSelect').value=current;
  populateVariants();
}
function populateVariants(){
  const c=bootstrap.cards.find(c=>c.id===+$('cardSelect').value);$('variantSelect').replaceChildren();
  if(!c)return;
  for(const v of c.variants)$('variantSelect').append(new Option(v.replaceAll('\\','/').split('/').slice(-2,-1)[0],v));
  $('variantSelect').value=c.variants.find(v=>v.includes('高清'))||c.variants[0];
}
async function home(){await flushDrafts();bootstrap=await api('/api/bootstrap');populateCards();showInstallation(bootstrap.installation);if(!libraryTimer)pollLibrary();$('recentProjects').replaceChildren();
  for(const p of bootstrap.projects){const button=document.createElement('button');const title=document.createElement('span');title.textContent=`${p.chineseName}〔${p.cardId}〕`;const info=document.createElement('small');info.textContent=`${p.completed}/${p.total} 已确认 · ${new Date(p.updatedUtc).toLocaleString()} · ${p.id}`;button.append(title,info);button.onclick=()=>action(()=>openProject(p.id));$('recentProjects').append(button);}
  if(!bootstrap.projects.length)$('recentProjects').textContent='尚无项目。新建后会自动保存在本机。';
  $('home').hidden=false;$('editor').hidden=true;message(bootstrap.samAvailable?'已找到本地素材库和 SAM 2.1。可新建项目，或继续已有进度。':'已找到本地素材库。当前未找到 SAM 2.1 模型，可正常使用套索。');
}
async function openProject(id){await flushDrafts();project=await api(`/api/projects/${id}`);$('home').hidden=true;$('editor').hidden=false;$('projectName').textContent=`${project.chineseName}〔${project.cardId}〕`;
  source=project.artwork?await image(fileUrl(project.artwork)):null;$('artworkPick').hidden=!!source;
  if(source){mask.width=source.width;mask.height=source.height;$('sourceSize').textContent=`${source.width} × ${source.height}`;}else{$('sourceSize').textContent='先选择新立绘';mask.width=1;mask.height=1;}
  await selectPart(project.currentPart,false);requestAnimationFrame(fitSource);saveText('项目已读取 · 自动保存');
}
function renderList(){
  const done=project.parts.filter(p=>p.status!=='pending').length;$('progressText').textContent=`${done} / ${project.parts.length} 项已确认`;$('progress').max=project.parts.length;$('progress').value=done;
  $('partList').replaceChildren();
  for(const [index,p] of project.parts.entries()){
    const button=document.createElement('button');button.className=p.number===part.number?'selected':'';button.dataset.number=p.number;
    const status=document.createElement('span');status.className='status';status.textContent=p.status==='replaced'?'✓':p.status==='original'?'○':p.status==='empty'?'∅':'·';status.title=p.status==='replaced'?'已替换':p.status==='original'?'已保留原图':p.status==='empty'?'已确认为空素材':'未确认';
    const thumb=document.createElement('img');thumb.src=fileUrl(`templates/${p.key}/reference.png`,false);thumb.alt='';
    const text=document.createElement('span');text.textContent=`${p.number}. ${p.label}`;const small=document.createElement('small');small.textContent=p.name;text.append(small);button.append(status,thumb,text);button.onclick=()=>action(()=>selectPart(index));$('partList').append(button);
  }
}
async function selectPart(index,persist=true){
  await flushDrafts();const generation=++loadGeneration;part=project.parts[index];project.currentPart=index;cutout=null;undo=[];dirtyMask=false;tintDirty=true;polygon=[];pointer=null;
  if(source){mask.width=source.width;mask.height=source.height;}
  const base=`templates/${part.key}/`;
  [reference,mesh]=await Promise.all([image(fileUrl(base+'reference.png')),image(fileUrl(base+'mesh.png'))]);
  if(generation!==loadGeneration)return;
  $('reference').src=reference.src;$('stepNumber').textContent=`部件 ${part.number} / ${project.parts.length}`;$('partTitle').textContent=part.label;$('partSize').textContent=`${part.width} × ${part.height}`;
  $('partDescription').textContent=`${part.name} · ${part.type} · ${part.page} · ${project.variant.replaceAll('\\','/').split('/').slice(-2,-1)[0]}`;
  $('partHint').textContent=`本步圈选「${part.label}」。请按上图的内容和方向确定范围，尽量保留关节和遮挡边缘；只选当前部件，缺少的隐藏部分需要另行补画。`;
  $('revisionSelect').replaceChildren();for(const [i,r] of part.revisions.entries())$('revisionSelect').append(new Option(`第 ${i+1} 次确认 · ${r.split('/').at(-2)}`,r));
  if(part.confirmedRevision)$('revisionSelect').value=part.confirmedRevision;
  prepareShape();resetPartReference();
  if(source&&part.draftMask){const m=await image(fileUrl(part.draftMask));mask.getContext('2d').drawImage(m,0,0);}
  if(part.draftCutout)cutout=await image(fileUrl(part.draftCutout));
  setMode('manual','manualBrushButton');syncPlacement();renderList();renderSource();renderAligned();
  if(persist)await api(`/api/projects/${project.id}/step`,{number:index});
  if(window.updatePartReferences)window.updatePartReferences(project.id,token,part);
  saveText('进度已保存');setControls();message(part.status==='pending'?'参考当前部件，在立绘中圈选，然后对齐并确认。':`当前部件${part.status==='replaced'?'已替换':part.status==='empty'?'已替换为空素材':'已保留原图'}。可以重新圈选，确认后会增加一个历史版本。`);
}
function fitSource(){if(!source)return;const box=$('sourceHost').getBoundingClientRect();view.scale=Math.min((box.width-20)/source.width,(box.height-20)/source.height);view.x=(box.width-source.width*view.scale)/2;view.y=(box.height-source.height*view.scale)/2;renderSource();}
function renderSource(){
  if(!$('sourceHost'))return;
  const box=$('sourceHost').getBoundingClientRect();const dpr=devicePixelRatio||1;const cw=Math.max(1,Math.round(box.width*dpr)),ch=Math.max(1,Math.round(box.height*dpr));if(canvas.width!==cw)canvas.width=cw;if(canvas.height!==ch)canvas.height=ch;ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,box.width,box.height);if(!source){const layer=$('tailFrameReference');if(layer?.dataset.ready==='true'&&$('tailLayerVisible').checked){const scale=Math.min(box.width/layer.width,box.height/layer.height);ctx.globalAlpha=+$('tailLayerOpacity').value;ctx.drawImage(layer,(box.width-layer.width*scale)/2,(box.height-layer.height*scale)/2,layer.width*scale,layer.height*scale);ctx.globalAlpha=1;}return;}
  $('sourceZoomText').textContent=Math.round(view.scale*100)+'%';ctx.save();ctx.translate(view.x,view.y);ctx.scale(view.scale,view.scale);ctx.globalAlpha=+$('backgroundDim').value;ctx.drawImage(source,0,0);ctx.globalAlpha=1;
  const layer=$('tailFrameReference');if(layer?.dataset.ready==='true'&&$('tailLayerVisible').checked){const scale=Math.min(source.width/layer.width,source.height/layer.height);ctx.save();ctx.globalAlpha=+$('tailLayerOpacity').value;ctx.drawImage(layer,(source.width-layer.width*scale)/2,(source.height-layer.height*scale)/2,layer.width*scale,layer.height*scale);ctx.restore();}
  if(tintDirty){tint.width=mask.width;tint.height=mask.height;const t=tint.getContext('2d');t.drawImage(mask,0,0);t.globalCompositeOperation='source-in';t.fillStyle=$('overlayColor').value;t.fillRect(0,0,tint.width,tint.height);t.globalCompositeOperation='destination-in';t.drawImage(source,0,0);selectedView.width=mask.width;selectedView.height=mask.height;const sv=selectedView.getContext('2d');sv.drawImage(source,0,0);sv.globalCompositeOperation='destination-in';sv.drawImage(mask,0,0);tintDirty=false;}ctx.drawImage(selectedView,0,0);ctx.globalAlpha=+$('overlayAlpha').value;ctx.drawImage(tint,0,0);ctx.globalAlpha=1;if(pointer?.kind==='brush'){ctx.globalAlpha=.6;ctx.drawImage(hint,0,0);ctx.globalAlpha=1;}
  if(polygon.length){ctx.beginPath();ctx.moveTo(...polygon[0]);for(const p of polygon.slice(1))ctx.lineTo(...p);ctx.lineWidth=2/view.scale;ctx.strokeStyle='#8ef2de';ctx.stroke();}
  if(mode==='shape'&&shapeTemplate){ctx.save();ctx.globalAlpha=.15;ctx.drawImage(shapeTemplate,shape.x-shapeTemplate.width*shape.scale/2,shape.y-shapeTemplate.height*shape.scale/2,shapeTemplate.width*shape.scale,shapeTemplate.height*shape.scale);ctx.globalAlpha=1;ctx.strokeStyle='#fff267';ctx.lineWidth=1.5/view.scale;ctx.strokeRect(shape.x-shapeTemplate.width*shape.scale/2,shape.y-shapeTemplate.height*shape.scale/2,shapeTemplate.width*shape.scale,shapeTemplate.height*shape.scale);ctx.restore();}
  ctx.restore();
}
function localPoint(e){const r=canvas.getBoundingClientRect();return[(e.clientX-r.left-view.x)/view.scale,(e.clientY-r.top-view.y)/view.scale];}
function pushUndo(){undo.push(mask.toDataURL());if(undo.length>15)undo.shift();}
function dirtySelection(){dirtyMask=true;tintDirty=true;cutout=null;part.draftCutout='';renderAligned();setControls();queueMaskSave();}
function queueMaskSave(){
  const path=partApi('draft-mask'),data=mask.toDataURL(), p=part;
  saveText('正在保存选区…');draftQueue=draftQueue.then(()=>api(path,{mask:data})).then(()=>{p.draftMask=`draft/${p.key}/mask.png`;saveText('选区已保存')}).catch(e=>{saveText('保存失败');message(e.message,true);throw e;});
}
async function flushDrafts(){if(placementTimer){clearTimeout(placementTimer);placementTimer=null;queuePlacementSave();}await draftQueue;}
function queuePlacementSave(){if(!project||!part||!cutout)return;const path=partApi('placement');const data=structuredClone(part.placement);saveText('正在保存对齐…');draftQueue=draftQueue.then(()=>api(path,data)).then(()=>saveText('对齐已保存')).catch(e=>{message(e.message,true);throw e;});}
function paintHint(point,previous){const h=hint.getContext('2d');h.strokeStyle=$('overlayColor').value;h.fillStyle=$('overlayColor').value;h.lineWidth=+$('brushSize').value;h.lineCap='round';h.lineJoin='round';if(previous){h.beginPath();h.moveTo(...previous);h.lineTo(...point);h.stroke();}else{h.beginPath();h.arc(...point,h.lineWidth/2,0,Math.PI*2);h.fill();}}
canvas.addEventListener('contextmenu',e=>e.preventDefault());
canvas.addEventListener('pointerdown',e=>{
 if(busy||!source)return;canvas.focus({preventScroll:true});canvas.setPointerCapture(e.pointerId);e.preventDefault();
 if(e.button===1||space){pointer={kind:'pan',x:e.clientX,y:e.clientY,originalX:view.x,originalY:view.y};return;}
 if(e.button!==0&&e.button!==2)return;
 const point=localPoint(e);const op=e.button===2?'subtract':'add';
 if($('tailLayerDrag').checked&&e.button===0){pointer={kind:'tailLayer',last:point};return;}
 if(mode==='shape'){strokeOperation=op;shape.x=point[0];shape.y=point[1];syncShape();pointer={kind:'shape'};renderSource();return;}
 if(mode==='point'){action(()=>runSam([[point[0],point[1],1]],op));return;}
 strokeOperation=op;hint.width=mask.width;hint.height=mask.height;
 if(mode==='sam'||mode==='manual'){pointer={kind:'brush',last:point};paintHint(point);}
 else{pointer={kind:'lasso'};polygon=[point];}renderSource();
});
canvas.addEventListener('pointermove',e=>{if(!pointer)return;if(pointer.kind==='pan'){view.x=pointer.originalX+e.clientX-pointer.x;view.y=pointer.originalY+e.clientY-pointer.y;}else if(pointer.kind==='tailLayer'){const pt=localPoint(e),layer=$('tailFrameReference'),scale=Math.min(source.width/layer.width,source.height/layer.height);window.panTailReference?.((pt[0]-pointer.last[0])/scale,(pt[1]-pointer.last[1])/scale);pointer.last=pt;}else if(pointer.kind==='shape'){const p=localPoint(e);shape.x=p[0];shape.y=p[1];syncShape();}else if(pointer.kind==='brush'){const pt=localPoint(e);paintHint(pt,pointer.last);pointer.last=pt;}else polygon.push(localPoint(e));renderSource();});
function mergeSelection(selection,operation){pushUndo();const m=mask.getContext('2d');m.save();m.globalCompositeOperation=operation==='subtract'?'destination-out':'source-over';m.drawImage(selection,0,0);m.restore();dirtySelection();renderSource();}
async function finishPolygon(){if(polygon.length>=3){hint.width=mask.width;hint.height=mask.height;const h=hint.getContext('2d');h.fillStyle='#fff';h.beginPath();h.moveTo(...polygon[0]);for(const point of polygon.slice(1))h.lineTo(...point);h.closePath();h.fill();polygon=[];await action(()=>runSam([],strokeOperation,hint));}polygon=[];renderSource();}
canvas.addEventListener('pointerup',async()=>{const kind=pointer?.kind;pointer=null;if(kind==='shape')applyShape(strokeOperation);else if(kind==='lasso')await finishPolygon();else if(kind==='brush'){if(mode==='manual')mergeSelection(hint,strokeOperation);else await action(()=>runSam([],strokeOperation,hint));}polygon=[];renderSource();});
canvas.addEventListener('keydown',e=>{if(e.key==='Escape'){polygon=[];renderSource();}});
canvas.addEventListener('pointercancel',()=>{pointer=null;polygon=[];renderSource();});
canvas.addEventListener('wheel',e=>{if(!source)return;e.preventDefault();if($('tailLayerDrag').checked){window.zoomTailReference?.(e.deltaY<0?1.1:1/1.1);return;}const r=canvas.getBoundingClientRect(),point=localPoint(e);const scale=Math.max(.015,Math.min(12,view.scale*Math.exp(-e.deltaY*.0015)));view.scale=scale;view.x=e.clientX-r.left-point[0]*scale;view.y=e.clientY-r.top-point[1]*scale;renderSource();},{passive:false});
window.addEventListener('keydown',e=>{if(e.code==='Space'&&!['INPUT','SELECT','TEXTAREA'].includes(document.activeElement.tagName)){space=true;e.preventDefault();}});window.addEventListener('keyup',e=>{if(e.code==='Space')space=false;});window.addEventListener('blur',()=>{space=false;pointer=null;polygon=[];});
async function runSam(points=[],operation='replace',prompt=mask){
  if(!source)return;await flushDrafts();message('SAM 正在本机计算，请稍候…');
  const blob=await api(`/api/projects/${project.id}/sam`,{mask:prompt.toDataURL(),points,limitToHint:points.length===0&&$('samLimit').checked});const url=URL.createObjectURL(blob);
  try{const prediction=await image(url);pushUndo();const m=mask.getContext('2d');if(operation==='replace')m.clearRect(0,0,mask.width,mask.height);m.save();m.globalCompositeOperation=operation==='subtract'?'destination-out':'source-over';if(operation==='subtract'){const hard=document.createElement('canvas');hard.width=prediction.width;hard.height=prediction.height;const hc=hard.getContext('2d');hc.drawImage(prediction,0,0);const pixels=hc.getImageData(0,0,hard.width,hard.height);for(let i=3;i<pixels.data.length;i+=4)pixels.data[i]=pixels.data[i]>0?255:0;hc.putImageData(pixels,0,0);m.drawImage(hard,0,0);}else m.drawImage(prediction,0,0);m.restore();dirtySelection();renderSource();message('SAM 结果已加入选区。可继续套索修正，再生成对齐预览。');}finally{URL.revokeObjectURL(url);}
}
function syncPlacement(){$('cutoutPercent').value=cutout?Math.round(part.placement.width/cutout.width*10000)/100:'';$('cutoutZoomText').textContent=cutout?Math.round(part.placement.width/cutout.width*100)+'%':'—';for(const k of ['X','Y','Width','Height','Rotation'])$('place'+k).value=Math.round((part.placement[k.toLowerCase()]||0)*100)/100;$('placeFlip').checked=part.placement.flip;}
function readPlacement(){for(const k of ['X','Y','Width','Height','Rotation']){const n=+$('place'+k).value;if(Number.isFinite(n))part.placement[k.toLowerCase()]=k==='Width'||k==='Height'?Math.max(1,n):n;}part.placement.flip=$('placeFlip').checked;$('cutoutPercent').value=cutout?Math.round(part.placement.width/cutout.width*10000)/100:'';renderAligned();if(placementTimer)clearTimeout(placementTimer);placementTimer=setTimeout(()=>{placementTimer=null;queuePlacementSave()},350);}
function renderRawAlignment(){aligned.width=part.width;aligned.height=part.height;const a=aligned.getContext('2d');a.clearRect(0,0,aligned.width,aligned.height);if(cutout){const p=part.placement;a.save();a.translate(p.x,p.y);a.rotate(p.rotation*Math.PI/180);a.scale(p.flip?-1:1,1);a.drawImage(cutout,-p.width/2,-p.height/2,p.width,p.height);a.restore();}return aligned;}
function renderAligned(){if(!part)return;$('cutoutPreview').hidden=!cutout;if(cutout)$('cutoutImage').src=cutout.src;else $('cutoutImage').removeAttribute('src');const target=$('alignmentCanvas');target.width=part.width;target.height=part.height;const a=target.getContext('2d');a.clearRect(0,0,target.width,target.height);
  if(reference&&$('ghostToggle').checked){a.globalAlpha=cutout?0.25:1;a.drawImage(reference,0,0);a.globalAlpha=1;}
  if(cutout){renderRawAlignment();masked.width=part.width;masked.height=part.height;const m=masked.getContext('2d');m.drawImage(aligned,0,0);if($('clipToggle').checked){m.globalCompositeOperation='destination-in';m.drawImage(mesh,0,0);}a.drawImage(masked,0,0);}
  $('alignmentState').textContent=cutout?'已按选区裁片，可拖动或缩放':part.status==='empty'?'此部件已确认为全透明':'等待涂抹选区';
}
let alignDrag;
$('alignmentCanvas').addEventListener('pointerdown',e=>{if(busy||!cutout)return;const r=e.currentTarget.getBoundingClientRect();alignDrag={x:e.clientX,y:e.clientY,px:part.placement.x,py:part.placement.y,sx:part.width/r.width,sy:part.height/r.height};e.currentTarget.setPointerCapture(e.pointerId);});
$('alignmentCanvas').addEventListener('pointermove',e=>{if(!alignDrag)return;part.placement.x=alignDrag.px+(e.clientX-alignDrag.x)*alignDrag.sx;part.placement.y=alignDrag.py+(e.clientY-alignDrag.y)*alignDrag.sy;syncPlacement();renderAligned();});
$('alignmentCanvas').addEventListener('pointerup',()=>{if(alignDrag){alignDrag=null;queuePlacementSave();}});
async function complete(kind){await flushDrafts();const next=await api(partApi(kind),kind==='confirm'?{image:'',placement:part.placement}:{});project=next;const pending=project.parts.findIndex((p,i)=>i>project.currentPart-1&&p.status==='pending');const any=project.parts.findIndex(p=>p.status==='pending');const index=pending>=0?pending:any>=0?any:Math.min(project.currentPart,project.parts.length-1);await selectPart(index);if(any<0){message('全部部件已确认，可以预览或导出替换文件。');$('completionDialog').showModal();}}
async function exportPackage(final){
 await flushDrafts();message(final?'正在生成替换文件与原始文件…':'正在生成整体预览…');
 latestExport=await api(`/api/projects/${project.id}/${final?'export':'preview'}`,{});
 if(!final){await window.openAnimationPreview(project.id,token);message(`已保存：${latestExport.folder}`);return;}
 try{message('正在渲染替换后尾帧并完成导出…');const image=await window.renderExportTailFrame(project.id,token,latestExport.relative);latestExport=await api(`/api/projects/${project.id}/export-tail`,{revision:latestExport.relative,image});}
 catch(e){throw Error('尾帧未能生成，导出尚未完成，请重试：'+e.message);}
 $('exportTitle').textContent='替换文件与尾帧已导出';$('exportSummary').textContent=`${latestExport.changes}个替换部件，${latestExport.bundleCount}个游戏文件，已同步${latestExport.companionCount}组低清。`;
 const install=await api('/api/settings');$('installTarget').textContent=install.gameRoot+'/LocalData/'+install.account;
 $('exportDialog').showModal();message(`已保存：${latestExport.folder}`);
}
$('cardSearch').oninput=populateCards;$('cardSelect').onchange=populateVariants;
$('createButton').onclick=()=>action(async()=>{if(!$('cardSelect').value)throw Error('请先选择卡片');message('正在读取骨骼并准备原部件参考…');const p=await api('/api/projects',{cardId:+$('cardSelect').value,variant:$('variantSelect').value});await openProject(p.id);});
$('artworkInput').onchange=()=>action(async()=>{const f=$('artworkInput').files[0];if(!f)return;message('正在保存立绘到本机项目…');project=await api(`/api/projects/${project.id}/artwork`,f,true);await openProject(project.id);});
$('homeButton').onclick=()=>action(home);$('quitButton').onclick=()=>action(async()=>{if(!confirm('保存进度并关闭本机工作流服务？'))return;await flushDrafts();await api('/api/quit',{});document.body.innerHTML='<main style="padding:50px"><h1>工作流已关闭</h1><p>进度已保存在本机。下次双击入口即可继续。</p></main>';});
function setMode(value,id){mode=value;polygon=[];for(const b of ['lassoButton','pointButton','samModeButton','manualBrushButton','shapeButton'])$(b).classList.toggle('active',b===id);$('shapeControls').hidden=value!=='shape';renderSource();}
$('lassoButton').onclick=()=>setMode('lasso','lassoButton');$('pointButton').onclick=()=>{setMode('point','pointButton');message('点击部件内部：左键加入 SAM 预测区域，右键减去预测区域。点选不受套索/涂选范围限制。');};$('samModeButton').onclick=()=>{setMode('sam','samModeButton');message('涂满所需部位，松开后由本地 SAM 细化。左键添加，右键减去。');};$('manualBrushButton').onclick=()=>{setMode('manual','manualBrushButton');message('严格涂抹：直接使用涂抹区域，不调用 SAM。左键保留像素，右键清除像素；空白保持透明。');};
$('fitButton').onclick=fitSource;$('clearButton').onclick=()=>{if(!source||busy)return;pushUndo();mask.getContext('2d').clearRect(0,0,mask.width,mask.height);dirtySelection();renderSource();};
$('undoButton').onclick=()=>action(async()=>{if(!undo.length)return;const previous=await image(undo.pop());mask.getContext('2d').clearRect(0,0,mask.width,mask.height);mask.getContext('2d').drawImage(previous,0,0);dirtySelection();renderSource();});
$('refineButton').onclick=()=>action(()=>runSam());$('generateButton').onclick=()=>action(async()=>{await flushDrafts();const result=await api(partApi('selection'),{mask:mask.toDataURL()});Object.assign(part,result.part);cutout=await image(fileUrl(part.draftCutout));dirtyMask=false;syncPlacement();renderAligned();message('调整位置、尺寸与角度。关闭原图参考后检查新部件，确认后进入下一项。');});
for(const input of document.querySelectorAll('.placement input'))input.oninput=readPlacement;
$('ghostToggle').onchange=renderAligned;$('clipToggle').onchange=renderAligned;
$('confirmButton').onclick=()=>action(()=>complete('confirm'));$('keepButton').onclick=()=>action(()=>complete('keep'));$('emptyButton').onclick=()=>action(()=>complete('empty'));
$('previousButton').onclick=()=>action(()=>selectPart(Math.max(0,project.currentPart-1)));$('nextButton').onclick=()=>action(()=>selectPart(Math.min(project.parts.length-1,project.currentPart+1)));
$('restoreButton').onclick=()=>action(async()=>{await flushDrafts();project=await api(partApi('restore'),{revision:$('revisionSelect').value});await selectPart(project.currentPart);message('已恢复选中的确认版本。后续部件和其他历史版本均已保留。');});
$('previewButton').onclick=()=>action(()=>exportPackage(false));$('exportButton').onclick=()=>action(()=>exportPackage(true));
$('folderButton').onclick=()=>action(()=>api(`/api/projects/${project.id}/open-export`,{revision:latestExport.relative}));$('closeExportButton').onclick=()=>$('exportDialog').close();
new ResizeObserver(()=>renderSource()).observe($('sourceHost'));
window.addEventListener('beforeunload',e=>{if(busy||placementTimer){e.preventDefault();e.returnValue='';}});
home().then(async()=>{const initial=new URLSearchParams(location.search).get('project');if(initial)await action(()=>openProject(initial));}).catch(e=>message(e.message,true));

for(const id of ['backgroundDim','overlayAlpha','overlayColor']){const saved=localStorage.getItem('selection-'+id);if(saved!==null)$(id).value=id==='overlayColor'&&saved==='#38edcf'?'#ff2038':id==='overlayAlpha'&&(saved==='0.35'||saved==='0.65')?'0.85':saved;$(id).oninput=()=>{localStorage.setItem('selection-'+id,$(id).value);tintDirty=true;renderSource();};}

canvas.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();$('undoButton').click();}});

function zoomSource(factor){if(!source||busy)return;const r=$('sourceHost').getBoundingClientRect(),cx=r.width/2,cy=r.height/2;const scale=Math.max(.015,Math.min(12,view.scale*factor)),ratio=scale/view.scale;view.x=cx-(cx-view.x)*ratio;view.y=cy-(cy-view.y)*ratio;view.scale=scale;renderSource();}
$('sourceZoomIn').onclick=()=>zoomSource(1.25);$('sourceZoomOut').onclick=()=>zoomSource(.8);
function zoomCutout(factor,actual=false){if(!cutout||busy)return;part.placement.width=actual?cutout.width:Math.max(1,part.placement.width*factor);part.placement.height=actual?cutout.height:Math.max(1,part.placement.height*factor);syncPlacement();renderAligned();queuePlacementSave();}
$('cutoutZoomIn').onclick=()=>zoomCutout(1.1);$('cutoutZoomOut').onclick=()=>zoomCutout(1/1.1);$('cutoutActualSize').onclick=()=>zoomCutout(1,true);

$('cutoutPercent').oninput=()=>{if(!cutout||busy)return;const value=Number($('cutoutPercent').value);if(!Number.isFinite(value)||value<.1||value>10000){return;}part.placement.width=cutout.width*value/100;part.placement.height=cutout.height*value/100;syncPlacement();renderAligned();queuePlacementSave();};
$('cutoutPercent').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();$('cutoutPercent').blur();}};
$('gamePreviewButton').onclick=()=>action(async()=>{$('exportDialog').close();await window.openAnimationPreview(project.id,token);});

let libraryTimer=null,libraryRunning=false,libraryLastJob='';
function showInstallation(info){if(!info)return;$('gameRootInput').value=info.gameRoot||'';$('gameAccountSelect').replaceChildren(...info.accounts.map(a=>new Option(a,a)));$('gameAccountSelect').value=info.account;$('libraryInfo').textContent=`目录已收录 ${info.libraryCards} 张，其中 ${bootstrap.cards.length} 张可逐部件编辑。启动时不扫描；打开卡片时临时提取所需游戏资源。`;}
function updateLibraryDisplay(s){libraryRunning=s.running;$('libraryPhase').textContent=({idle:'现有素材立即可用。点击更新后才检查游戏文件。',scan:'正在检查新增或变动的游戏资源…',extract:'正在增量提取动画素材，并更新卡名目录…',completed:'素材及卡名目录已更新。',partial:'更新完成，部分资源未能提取，详情见记录。',cancelled:'更新已停止，已完成的素材保留。',failed:'更新失败：'+s.error})[s.phase]||s.phase;$('libraryLog').textContent=s.messages.join('\n');for(const id of ['gameRootInput','gameAccountSelect','browseGameButton','saveGameButton','updateLibraryButton'])$(id).disabled=s.running;$('cancelLibraryButton').disabled=!s.running;}
async function pollLibrary(){if(libraryTimer){clearTimeout(libraryTimer);libraryTimer=null;}try{const state=await api('/api/library/status');updateLibraryDisplay(state);if(state.running)libraryTimer=setTimeout(pollLibrary,1200);else if(state.job&&state.job!==libraryLastJob){libraryLastJob=state.job;bootstrap=await api('/api/bootstrap');populateCards();showInstallation(bootstrap.installation);}}catch(e){$('libraryPhase').textContent=e.message;}}
$('browseGameButton').onclick=()=>action(async()=>{const result=await api('/api/settings/browse',{});if(!result.path)return;const info=await api('/api/settings',{gameRoot:result.path,account:$('gameAccountSelect').value});showInstallation(info);message('游戏目录已保存；现有素材继续使用，不会自动扫描。');});
$('saveGameButton').onclick=()=>action(async()=>{const info=await api('/api/settings',{gameRoot:$('gameRootInput').value,account:$('gameAccountSelect').value});showInstallation(info);message('游戏目录与账号已保存。');});
$('updateLibraryButton').onclick=()=>action(async()=>{const state=await api('/api/library/update',{});updateLibraryDisplay(state);pollLibrary();message('已开始增量更新，完成后自动刷新卡名目录。');});
$('cancelLibraryButton').onclick=()=>action(()=>api('/api/library/cancel',{}));
$('clearCacheButton').onclick=()=>action(async()=>{const result=await api('/api/library/cache/clear',{});message('已清理 '+result.removed+' 组未使用缓存；现有项目引用的素材已保留。');});

$('completedBack').onclick=()=>$('completionDialog').close();
$('completedPreview').onclick=()=>{$('completionDialog').close();action(()=>exportPackage(false));};
$('completedExport').onclick=()=>{$('completionDialog').close();action(()=>exportPackage(true));};
window.addEventListener('references-ready',()=>{if(project&&part)window.updatePartReferences(project.id,token,part);});

// The shape stamp uses only the original visible silhouette, never its bounding rectangle.
let shapeTemplate=null,shape={x:0,y:0,scale:1};
function syncShape(){$('shapeScale').value=Math.round(shape.scale*10000)/100;$('shapeX').value=Math.round(shape.x);$('shapeY').value=Math.round(shape.y);}
function prepareShape(){
 shapeTemplate=null;if(!source||!reference)return;
 const full=document.createElement('canvas');full.width=reference.width;full.height=reference.height;const c=full.getContext('2d');c.drawImage(reference,0,0);c.globalCompositeOperation='destination-in';c.drawImage(mesh,0,0);
 const pixels=c.getImageData(0,0,full.width,full.height);let x0=full.width,y0=full.height,x1=-1,y1=-1;
 for(let y=0;y<full.height;y++)for(let x=0;x<full.width;x++){const i=(y*full.width+x)*4;const alpha=pixels.data[i+3]>8?255:0;pixels.data[i]=255;pixels.data[i+1]=32;pixels.data[i+2]=56;pixels.data[i+3]=alpha;if(alpha){x0=Math.min(x0,x);y0=Math.min(y0,y);x1=Math.max(x1,x);y1=Math.max(y1,y);}}
 if(x1<0){return;}c.globalCompositeOperation='source-over';c.putImageData(pixels,0,0);shapeTemplate=document.createElement('canvas');shapeTemplate.width=x1-x0+1;shapeTemplate.height=y1-y0+1;shapeTemplate.getContext('2d').drawImage(full,x0,y0,shapeTemplate.width,shapeTemplate.height,0,0,shapeTemplate.width,shapeTemplate.height);fitShape();
}
function fitShape(){if(!source||!shapeTemplate)return;shape={x:source.width/2,y:source.height/2,scale:Math.min(1,source.width*.6/shapeTemplate.width,source.height*.6/shapeTemplate.height)};syncShape();renderSource();}
function applyShape(operation){if(busy||!source||!shapeTemplate)return;const stamp=document.createElement('canvas');stamp.width=mask.width;stamp.height=mask.height;stamp.getContext('2d').drawImage(shapeTemplate,shape.x-shapeTemplate.width*shape.scale/2,shape.y-shapeTemplate.height*shape.scale/2,shapeTemplate.width*shape.scale,shapeTemplate.height*shape.scale);mergeSelection(stamp,operation);}
$('shapeButton').onclick=()=>{if(!shapeTemplate)prepareShape();if(!shapeTemplate){message('此部件没有可用的可见轮廓。',true);return;}setMode('shape','shapeButton');message('按原部件轮廓选取：调整比例后，左键拖放加选、右键拖放减选。可撤销并切回涂抹继续修边。');};
for(const id of ['shapeScale','shapeX','shapeY'])$(id).oninput=()=>{if(busy||!source)return;const scale=+$('shapeScale').value/100,x=+$('shapeX').value,y=+$('shapeY').value;if(scale<.01||scale>10||!Number.isFinite(scale+x+y))return;shape={x,y,scale};renderSource();};
$('shapeFit').onclick=fitShape;$('shapeAdd').onclick=()=>applyShape('add');$('shapeSubtract').onclick=()=>applyShape('subtract');
let partReferenceView={scale:1,x:0,y:0},partReferenceDrag;
function renderPartReference(){const v=partReferenceView;$('reference').style.transform=`translate(${v.x}px,${v.y}px) scale(${v.scale})`;$('partReferenceZoom').value=Math.round(v.scale*100);}
function resetPartReference(){partReferenceView={scale:1,x:0,y:0};renderPartReference();}
$('partReferenceZoom').oninput=()=>{const n=+$('partReferenceZoom').value;if(n>=25&&n<=400){partReferenceView.scale=n/100;renderPartReference();}};$('partReferenceFit').onclick=resetPartReference;
const partRefHost=$('partReferenceHost');partRefHost.onwheel=e=>{e.preventDefault();partReferenceView.scale=Math.max(.25,Math.min(4,partReferenceView.scale*(e.deltaY<0?1.1:1/1.1)));renderPartReference();};
partRefHost.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();partRefHost.setPointerCapture(e.pointerId);partReferenceDrag={x:e.clientX,y:e.clientY,px:partReferenceView.x,py:partReferenceView.y};};partRefHost.onpointermove=e=>{if(!partReferenceDrag)return;partReferenceView.x=partReferenceDrag.px+e.clientX-partReferenceDrag.x;partReferenceView.y=partReferenceDrag.py+e.clientY-partReferenceDrag.y;renderPartReference();};partRefHost.onpointerup=partRefHost.onpointercancel=()=>partReferenceDrag=null;

$('keepPendingButton').onclick=()=>action(async()=>{await flushDrafts();project=await api(`/api/projects/${project.id}/keep-pending`,{});await selectPart(project.currentPart);message('所有未确认部件已保留原图，已有替换不变。');$('completionDialog').showModal();});
for(const id of ['tailLayerVisible','tailLayerOpacity'])$(id).oninput=renderSource;
window.addEventListener('tail-layer-updated',renderSource);
