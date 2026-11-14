import { Pose, parseAtlas, readSkeletonJson, regionPlacement } from './src/core/index.js';

const geometryTolerance = 0.2;
const alphaTolerance = 0.001;
const colorTolerance = 0.005;
const probeResolution = 0.0001;
const physicsFps = 60;
const showTypes = new Set(['region', 'mesh', 'linkedmesh', 'clipping']);
const constraints = ['ik', 'transform', 'path', 'physics'];
const floats = new Float32Array(1);
const bits = new Uint32Array(floats.buffer);

function previousFloat(time) {
  floats[0] = time;
  bits[0]--;
  return floats[0];
}

function sampleSchedule(animation, duration, physics) {
  const authored = new Map();
  function visit(value) {
    if (!value || typeof value !== 'object') return;
    if (typeof value.time === 'number' && value.time >= 0) {
      const time = Math.fround(value.time);
      if (authored.has(time) && authored.get(time) !== value.time)
        throw Error('有两个关键帧时刻超出 Spine 4.2 可区分的时间精度。');
      authored.set(time, value.time);
    }
    for (const entry of Object.values(value)) visit(entry);
  }
  visit(animation);
  const times = new Set([0, Math.fround(duration)]);
  for (const time of authored.keys()) {
    times.add(time);
    if (time > 0) times.add(previousFloat(time));
  }
  for (let i = 1; i / 120 < duration; i++) times.add(Math.fround(i / 120));
  if (physics) {
    for (let i = 1; i / physicsFps <= duration; i++) {
      const time = Math.fround(i / physicsFps);
      times.add(time);
      times.add(previousFloat(time));
    }
  }
  return { times: Array.from(times).sort((a, b) => a - b), authored };
}

function attachedRegions(atlas) {
  return new Map(parseAtlas(atlas).pages.flatMap(page => page.regions.map(region => [region.name, region])));
}

function regionUvs(image, regions) {
  const region = regions.get(image);
  if (!region || !region.originalWidth || !region.originalHeight)
    throw Error(`图集缺少有效部件「${image}」。`);
  const { dst, originalWidth, originalHeight } = regionPlacement(region);
  const u0 = dst.x / originalWidth;
  const v0 = dst.y / originalHeight;
  const u1 = (dst.x + dst.width) / originalWidth;
  const v1 = (dst.y + dst.height) / originalHeight;
  return [u0, v0, u1, v0, u1, v1, u0, v1];
}

function positions(pose, rig, slot, att, uvs) {
  if (att.type !== 'region') return pose.worldVertices(rig, slot, att);
  const m = pose.attachmentWorld(rig, slot, att);
  const coordinates = [];
  for (let i = 0; i < uvs.length; i += 2) {
    const x = (uvs[i] - 0.5) * att.width;
    const y = (0.5 - uvs[i + 1]) * att.height;
    coordinates.push(m.a * x + m.c * y + m.tx, m.b * x + m.d * y + m.ty);
  }
  return coordinates;
}

const rounded = (n, places = 1000) => Math.round(n * places) / places;
const colorHex = (values) => values.map(value =>
  Math.round(Math.max(0, Math.min(value, 1)) * 255).toString(16).padStart(2, '0')).join('');

function prepare(input, rig, regions) {
  const output = structuredClone(input);
  for (const name of constraints) delete output[name];
  let carrier = '_mdRigby';
  while (output.bones.some(bone => bone.name === carrier)) carrier += '_';
  for (const bone of output.bones) bone.parent ??= carrier;
  output.bones.unshift({ name: carrier });

  const setup = new Pose(rig);
  const skin = output.skins.find(entry => entry.name === 'default');
  for (const name of constraints) delete skin[name];
  const meshes = [];
  for (const [index, slot] of rig.slots.entries()) {
    const attachments = skin.attachments?.[slot.name];
    if (!attachments) continue;
    const shiftSlot = slot.attachments.some(att => showTypes.has(att.type));
    if (shiftSlot) output.slots[index].bone = carrier;
    for (const att of slot.attachments) {
      const raw = attachments[att.name];
      if (!showTypes.has(att.type)) {
        if (shiftSlot) throw Error(`插槽「${slot.name}」混用了显示部件和辅助附件，暂不支持自动转换。`);
        // Weighted vertices store numeric bone indices, which move when the new root is added.
        if (att.weights) {
          let offset = 0;
          for (const weights of att.weights) {
            offset++;
            for (const _ of weights) {
              raw.vertices[offset]++;
              offset += 4;
            }
          }
        }
        continue;
      }
      if (att.sequence) throw Error(`附件「${att.name}」使用序列贴图，暂不支持自动转换。`);
      const mesh = rig.meshSource(index, att);
      if (!mesh) throw Error(`找不到链接网格「${att.name}」引用的父网格。`);
      const region = att.type === 'region';
      const clipping = att.type === 'clipping';
      let uvs;
      if (!clipping) {
        const bounds = regionUvs(att.image, regions);
        const [u0, v0, u1, , , v1] = bounds;
        // Rigby2D clamps each mesh vertex to the rectangle that contains this image.
        uvs = region ? bounds : mesh.uvs.map((value, i) => i % 2
          ? Math.max(v0, Math.min(v1, value)) : Math.max(u0, Math.min(u1, value)));
      }
      const vertices = positions(setup, rig, index, att, uvs).map(value => rounded(value));
      if (!vertices.length || vertices.some(value => !Number.isFinite(value)))
        throw Error(`附件「${att.name}」的顶点数据无效。`);
      if (clipping) attachments[att.name] = { ...raw, vertices };
      else {
        const shape = region ? { triangles: [0, 1, 2, 0, 2, 3], hull: 4 } : {
          triangles: mesh.triangles, hull: mesh.hull, ...(mesh.edges ? { edges: mesh.edges } : {})
        };
        attachments[att.name] = {
          type: 'mesh', path: att.image, ...(raw.color ? { color: raw.color } : {}),
          uvs, vertices, ...shape
        };
      }
      meshes.push({ index, name: att.name, uvs, vertices, attachment: att });
    }
  }
  return { output, meshes };
}

// Keep a frame when interpolating between its neighbours would move a vertex or color too far.
function reduce(times, frames, tolerance, pairs = false) {
  const keep = new Set([0, times.length - 1]);
  const pending = [[0, times.length - 1]];
  while (pending.length) {
    const [start, end] = pending.pop();
    if (end - start < 2) continue;
    const begin = frames[start], finish = frames[end];
    let worst = pairs ? tolerance * tolerance : tolerance;
    let index = -1;
    for (let i = start + 1; i < end; i++) {
      const weight = (times[i] - times[start]) / (times[end] - times[start]);
      const frame = frames[i];
      for (let k = 0; k < frame.length; k++) {
        const delta = frame[k] - (begin[k] + weight * (finish[k] - begin[k]));
        let error = Math.abs(delta);
        if (pairs) {
          k++;
          const dy = frame[k] - (begin[k] + weight * (finish[k] - begin[k]));
          error = delta * delta + dy * dy;
        }
        if (error > worst) {
          worst = error;
          index = i;
        }
      }
    }
    if (index !== -1) {
      keep.add(index);
      pending.push([start, index], [index, end]);
    }
  }
  return Array.from(keep).sort((a, b) => a - b);
}

function sparseFrame(time, frame, base) {
  const deltas = Array.from(frame, (value, i) => rounded(value - base[i]));
  const start = deltas.findIndex(value => value !== 0);
  if (start === -1) return key(time);
  let end = deltas.length;
  while (!deltas[end - 1]) end--;
  return { ...key(time), ...(start ? { offset: start } : {}), vertices: deltas.slice(start, end) };
}

function key(time) {
  return time === 0 ? {} : { time };
}

function poseAt(pose, rig, clip, time, delta = 0) {
  pose.reset(rig);
  pose.apply(rig, clip, time, 1);
  pose.physicsDelta = delta;
  pose.updateWorld(rig);
}

// Start a fresh playback for each clip and use fixed ticks, regardless of how many extra
// times are requested for geometry fitting or verification.
function playback(rig, clip, authored, physics) {
  const pose = new Pose(rig);
  pose.resetPhysics();
  poseAt(pose, rig, clip, 0);
  let tick = 0;
  return time => {
    if (physics) {
      for (let next = Math.fround((tick + 1) / physicsFps); next <= time; next = Math.fround((tick + 1) / physicsFps)) {
        poseAt(pose, rig, clip, authored.get(next) ?? next, 1 / physicsFps);
        tick++;
      }
    }
    poseAt(pose, rig, clip, authored.get(time) ?? time);
    return pose;
  };
}

function encodeClip(output, source, rig, clip, meshes, schedule, physics) {
  const { times, authored } = schedule;
  const animation = output.animations[clip.name];
  for (const name of constraints) delete animation[name];
  delete animation.deform;
  delete animation.attachments;
  delete animation.draworder;
  if (source.drawOrder ?? source.draworder) {
    animation.drawOrder = (source.drawOrder ?? source.draworder).map(frame => ({
      ...frame, time: Math.fround(frame.time ?? 0)
    }));
  }
  const tracks = new Map(clip.slots.map(entry => [entry.slot, entry]));
  const colors = rig.slots.map(() => []);
  const geometry = meshes.map(() => []);
  const evaluate = playback(rig, clip, authored, physics);
  for (const time of times) {
    const pose = evaluate(time);
    for (const index of tracks.keys()) {
      colors[index].push([...pose.color[index], ...(pose.dark[index] ?? [0, 0, 0])]);
    }
    for (const [i, mesh] of meshes.entries()) {
      geometry[i].push(Float32Array.from(positions(pose, rig, mesh.index, mesh.attachment, mesh.uvs)));
    }
  }
  const slots = {};
  for (const [i, track] of tracks) {
    const slot = rig.slots[i];
    const frames = colors[i];
    const timelines = {};
    if (track.attachment.length) {
      const switches = new Map([[0, track.attachment[0].value]]);
      for (const { time, value } of track.attachment) switches.set(Math.fround(time), value);
      timelines.attachment = Array.from(switches, ([time, name]) => ({ ...key(time), ...(name === null ? {} : { name }) }));
    }
    if (frames.some(frame => Math.abs(frame[3] - slot.color[3]) > 0.0001)) {
      const alpha = frames.map(frame => [frame[3]]);
      timelines.alpha = reduce(times, alpha, 0.00055).map(k => ({ ...key(times[k]), value: rounded(alpha[k][0], 100000) }));
    }
    const twoColor = track.rgba2.length > 0 || track.rgb2.length > 0;
    const range = twoColor ? [0, 1, 2, 4, 5, 6] : [0, 1, 2];
    const baseline = [...slot.color, ...(slot.dark ?? [0, 0, 0])];
    if (twoColor || frames.some(frame => range.some(c => frame[c] !== baseline[c]))) {
      const tint = frames.map(frame => range.map(c => frame[c]));
      const framesToKeep = reduce(times, tint, 0.0015);
      if (twoColor) {
        output.slots[i].dark ??= '000000';
        timelines.rgb2 = framesToKeep.map(k => ({
          ...key(times[k]), light: colorHex(tint[k].slice(0, 3)), dark: colorHex(tint[k].slice(3))
        }));
      } else timelines.rgb = framesToKeep.map(k => ({ ...key(times[k]), color: colorHex(tint[k]) }));
    }
    if (Object.keys(timelines).length) slots[slot.name] = timelines;
  }
  animation.slots = slots;
  const deform = Object.create(null);
  for (const [i, mesh] of meshes.entries()) {
    const frames = geometry[i];
    if (frames.some(frame => frame.some((value, k) => Math.abs(value - mesh.vertices[k]) > 0.01))) {
      const slot = rig.slots[mesh.index].name;
      const keys = reduce(times, frames, 0.155, true).map(k => sparseFrame(times[k], frames[k], mesh.vertices));
      (deform[slot] ??= Object.create(null))[mesh.name] = { deform: keys };
    }
  }
  if (Object.keys(deform).length) animation.attachments = { default: deform };
  // A flat or hidden clip can otherwise lose its duration when all visual timelines are constant.
  if (times.at(-1) > 0) {
    const carrier = output.bones[0].name;
    (animation.bones ??= {})[carrier] = { translate: [key(times.at(-1))] };
  }
}

function assertClose(source, target, max, description) {
  if (!Number.isFinite(target) || Math.abs(source - target) > max)
    throw Error(`${description}误差超出校验范围。`);
}

function verify(rig, clip, meshes, schedule, output, physics) {
  const baked = readSkeletonJson(output).skeleton;
  const targetClip = baked.animations.find(entry => entry.name === clip.name);
  assertClose(clip.duration, targetClip.duration, 0.00001, '动作时长');
  const target = new Pose(baked);
  const slots = new Map();
  for (const mesh of meshes) {
    if (!slots.has(mesh.index)) slots.set(mesh.index, new Map());
    slots.get(mesh.index).set(mesh.name, mesh);
  }
  let largest = 0;
  const missed = [];
  const { times, authored } = schedule;
  const probes = new Set(times);
  for (let i = 1; i < times.length; i++) {
    const a = times[i - 1], b = times[i];
    // Compare authored jumps on both sides at float32 precision. Between those points,
    // stop refining sub-frame intervals where the source curve solver's tolerance dominates.
    if (b - a < probeResolution) continue;
    for (const fraction of [0.37, 0.73]) probes.add(Math.fround(a + (b - a) * fraction));
  }
  const evaluate = playback(rig, clip, authored, physics);
  for (const time of Array.from(probes).sort((a, b) => a - b)) {
    const input = evaluate(time);
    poseAt(target, baked, targetClip, time);
    if (input.drawOrder.some((slot, i) => slot !== target.drawOrder[i]))
      throw Error(`「${clip.name}」的绘制顺序校验失败。`);
    let needsSample = false;
    for (const [slot, entries] of slots) {
      const name = input.attachment[slot];
      if (name !== target.attachment[slot]) throw Error(`「${rig.slots[slot].name}」的附件切换校验失败。`);
      const mesh = entries.get(name);
      if (!mesh) continue;
      const description = `「${mesh.name}」`;
      const expected = positions(input, rig, slot, mesh.attachment, mesh.uvs);
      const actual = target.worldVertices(baked, slot, target.slotAttachment(baked, slot));
      for (let i = 0; i < expected.length; i += 2) {
        const distance = Math.hypot(expected[i] - actual[i], expected[i + 1] - actual[i + 1]);
        if (!Number.isFinite(distance)) throw Error(`${description}含无效顶点。`);
        if (distance > geometryTolerance) needsSample = true;
        largest = Math.max(largest, distance);
      }
      if (Math.abs(input.color[slot][3] - target.color[slot][3]) > alphaTolerance) needsSample = true;
      for (let i = 0; i < 3; i++) {
        if (Math.abs(input.color[slot][i] - target.color[slot][i]) > colorTolerance ||
            Math.abs((input.dark[slot]?.[i] ?? 0) - (target.dark[slot]?.[i] ?? 0)) > colorTolerance) needsSample = true;
      }
    }
    if (needsSample) missed.push(time);
  }
  return { maxVertexError: largest, checkpoints: probes.size, missed };
}

export function convertSummon(input, atlas, progress = () => {}) {
  if (!/^4\.2(?:\.|$)/.test(input.skeleton?.spine))
    throw Error('请在 Rigby2D 中选择 Spine 4.2 JSON + Atlas 导出。');
  if (!Array.isArray(input.skins) || input.skins.length !== 1 || input.skins[0].name !== 'default')
    throw Error('自动转换目前支持单一 default 皮肤。');
  if (input.constraints?.length) throw Error('自动转换需要 Spine 4.2 的约束格式。');
  const physics = Boolean(input.physics?.length);
  const { skeleton: rig, warnings } = readSkeletonJson(input);
  if (warnings.length) throw Error('存在 Rigby2D 本地运行时无法读取的轨道：' + warnings[0]);
  if (!rig.animations.length) throw Error('素材 JSON 中没有可转换的动画。');
  const regions = attachedRegions(atlas);
  const { output, meshes } = prepare(input, rig, regions);
  let checkpoints = 0, maxVertexError = 0;
  for (const [i, clip] of rig.animations.entries()) {
    const source = input.animations[clip.name];
    progress(`正在采样动作（${i + 1}/${rig.animations.length}）「${clip.name}」…`);
    const schedule = sampleSchedule(source, clip.duration, physics);
    let report;
    for (let pass = 0; pass < 10; pass++) {
      encodeClip(output, source, rig, clip, meshes, schedule, physics);
      progress(`正在校验动作（${i + 1}/${rig.animations.length}）「${clip.name}」…`);
      report = verify(rig, clip, meshes, schedule, output, physics);
      if (!report.missed.length) break;
      const samples = new Set([...schedule.times, ...report.missed]);
      if (samples.size === schedule.times.length) break;
      schedule.times = Array.from(samples).sort((a, b) => a - b);
    }
    if (report.missed.length)
      throw Error(`动作「${clip.name}」在 ${report.missed[0].toFixed(6)} 秒附近未通过顶点或颜色校验。`);
    maxVertexError = Math.max(maxVertexError, report.maxVertexError);
    checkpoints += report.checkpoints;
  }
  return { skeleton: output, checkpoints, maxVertexError };
}
