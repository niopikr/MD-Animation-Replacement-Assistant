"""Extract card-specific Unity effects using an existing game index.

Requires Python 3.11+, UnityPy and Pillow. Game bundles are opened read-only.
The editable projects translate 2D sprites, skin weights and bound Timeline
curves. Particle systems, custom shaders and 3D rendering stay in the game.
"""
import argparse
from bisect import bisect_right
from collections import defaultdict
from functools import lru_cache
import csv
import hashlib
import html
import io
import json
import math
from pathlib import Path, PurePosixPath
import re
import struct
from urllib.parse import quote
import zipfile
import zlib

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

EFFECT = re.compile(r"/duel/timeline/card/(?:.*?/)?ef(\d+)/(.*)$", re.I)
SUPPORTED = {4, 10, 12, 25, 27}
UNIT = 100
FPS = 60


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode()


def safe(name):
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")[:75] or "素材"


def numbers(obj, names, defaults):
    return tuple(float(obj.get(k, v)) for k, v in zip(names, defaults))


def float32(value):
    return struct.unpack("<f", struct.pack("<I", value))[0]


def crc(value):
    return zlib.crc32(value.encode("utf-8"))


def angle(quaternion):
    x, y, z, w = quaternion
    return math.degrees(math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)))


def roundf(value):
    return round(value, 5) if math.isfinite(value) else 0


class Bundles:
    def __init__(self, index, game_root=None, account=None):
        self.paths = {}
        for b in index["Bundles"]:
            if game_root is None:
                path = Path(b["Path"])
            else:
                indexed = b["Path"].replace("\\", "/")
                local = re.search(r"/LocalData/[^/]+/(0000/.*)$", indexed, re.I)
                streaming = re.search(r"/(masterduel_Data/StreamingAssets/.*)$", indexed, re.I)
                if local:
                    tail = PurePosixPath(local[1])
                    if len(tail.parts) != 3 or not re.fullmatch(r"[0-9a-f]{2}/[0-9a-f]{8}", "/".join(tail.parts[1:]), re.I):
                        continue
                    path = game_root / "LocalData" / account / Path(*tail.parts)
                elif streaming:
                    tail = PurePosixPath(streaming[1])
                    if ".." in tail.parts or any(":" in part for part in tail.parts):
                        continue
                    path = game_root / Path(*tail.parts)
                else:
                    continue
            if path.is_file():
                self.paths[path.name] = path

    @lru_cache(maxsize=80)
    def open(self, bundle):
        env = UnityPy.load(str(self.paths[bundle]))
        return list(env.objects)

    @lru_cache(maxsize=400)
    def digest(self, bundle):
        with self.paths[bundle].open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def dependencies(self, roots):
        seen = set()
        queue = list(roots)
        for bundle in queue:
            if bundle in seen or bundle not in self.paths:
                continue
            seen.add(bundle)
            yield bundle
            for o in self.open(bundle):
                if o.type.name == "AssetBundle":
                    queue.extend(Path(x.replace("\\", "/")).name for x in o.read().m_Dependencies)


def resolve(owner, ptr, files):
    if not ptr or not ptr.get("m_PathID"):
        return None
    file = owner.assets_file
    external = ptr.get("m_FileID", 0)
    if external:
        if external > len(file.externals):
            return None
        file = files.get(PurePosixPath(file.externals[external - 1].path.replace("\\", "/")).name)
    return file.objects.get(ptr["m_PathID"]) if file else None


def key(obj):
    return (obj.assets_file.name, obj.path_id)


def tree(obj):
    return obj.read_typetree()


def file_index(obj):
    parent = obj.assets_file.parent
    return list(parent.files).index(obj.assets_file.name)


def atlas_bytes(regions, sizes):
    pages = defaultdict(list)
    for region, (page, x, y, w, h, ox, oy, ow, oh) in regions.items():
        pages[page].append((region, x, y, w, h, ox, oy, ow, oh))
    chunks = []
    for page, entries in pages.items():
        width, height = sizes[page]
        lines = [page, f"size:{width},{height}", "filter:Linear,Linear", "pma:false"]
        for name, x, y, w, h, ox, oy, ow, oh in entries:
            lines.extend([name, f"bounds:{x},{y},{w},{h}"])
            if (ox, oy, ow, oh) != (0, 0, w, h):
                lines.append(f"offsets:{ox},{oy},{ow},{oh}")
        chunks.append("\n".join(lines))
    return ("\n\n".join(chunks) + "\n").encode()


def skeleton(name, bones, slots, attachments, animations):
    return {
        "skeleton": {"spine": "4.2.43", "images": "./", "fps": FPS, "width": 1600, "height": 1200},
        "bones": bones, "slots": slots,
        "skins": [{"name": "default", "attachments": attachments}],
        "animations": animations,
    }


def sheet_preview(sizes):
    bones, slots, attachments, regions = [{"name": "root"}], [], {}, {}
    for i, (page, (w, h)) in enumerate(sizes.items()):
        name = Path(page).stem
        scale = min(1, 300 / max(w, h))
        x, y = (i % 4) * 330, -(i // 4) * 330
        slots.append({"name": name, "bone": "root", "attachment": name})
        attachments[name] = {name: {"width": w, "height": h, "x": x, "y": y, "scaleX": scale, "scaleY": scale}}
        regions[name] = (page, 0, 0, w, h, 0, 0, w, h)
    return skeleton("sheet", bones, slots, attachments, {}), regions


class Clip:
    def __init__(self, data):
        self.bindings = data["m_ClipBindingConstant"]["genericBindings"]
        clip = data["m_MuscleClip"]["m_Clip"]["data"]
        self.duration = float(data["m_MuscleClip"].get("m_StopTime", 0))
        self.stream = clip["m_StreamedClip"]["curveCount"]
        self.dense = clip["m_DenseClip"]
        self.const = clip["m_ConstantClip"]["data"]
        words = clip["m_StreamedClip"]["data"]
        self.keys = defaultdict(list)
        i = 0
        while i + 2 <= len(words):
            at, count = float32(words[i]), words[i + 1]
            i += 2
            if i + 5 * count > len(words):
                raise ValueError("invalid streamed clip")
            for _ in range(count):
                index = words[i]
                if math.isfinite(at) and -1e5 < at < 1e5:
                    self.keys[index].append((at, *[float32(n) for n in words[i + 1:i + 5]]))
                i += 5
        self.times = {k: [a[0] for a in values] for k, values in self.keys.items()}

    def value(self, index, at):
        if index < self.stream:
            keys = self.keys.get(index)
            if not keys:
                return None
            i = bisect_right(self.times[index], at + 1e-7) - 1
            if i < 0:
                return keys[0][-1]
            t, a, b, c, d = keys[i]
            dt = at - t
            return d if i == len(keys) - 1 else ((a * dt + b) * dt + c) * dt + d
        index -= self.stream
        count = self.dense["m_CurveCount"]
        if index < count:
            raw = self.dense["m_SampleArray"]
            frames = self.dense["m_FrameCount"]
            if frames <= 0:
                return None
            frame = min(max(0, (at - self.dense["m_BeginTime"]) * self.dense["m_SampleRate"]), frames - 1)
            lo, hi = int(frame), min(frames - 1, int(frame) + 1)
            a, b = raw[lo * count + index], raw[hi * count + index]
            return a + (b - a) * (frame - lo)
        index -= count
        return self.const[index] if index < len(self.const) else None


def component_name(o, files):
    d = tree(o)
    script = resolve(o, d.get("m_Script"), files)
    return script.peek_name() if script else ""


def bone_local(v):
    pos = numbers(v.get("m_LocalPosition", {}), "xyz", (0, 0, 0))
    rot = numbers(v.get("m_LocalRotation", {}), "xyzw", (0, 0, 0, 1))
    scale = numbers(v.get("m_LocalScale", {}), "xyz", (1, 1, 1))
    return [pos[0] * UNIT, pos[1] * UNIT, angle(rot), scale[0], scale[1]]


def timeline_animation(director, nodes, by_go, files, setup, slots_by_node, weighted_nodes, initial_active):
    d = tree(director)
    timeline = resolve(director, d.get("m_PlayableAsset"), files)
    if not timeline:
        return {}, 0, None
    clips, gates = [], {}
    end = 0
    for binding in d["m_SceneBindings"]:
        track = resolve(director, binding.get("key"), files)
        target = resolve(director, binding.get("value"), files)
        if not track or not target:
            continue
        t = tree(track)
        if t.get("m_Muted"):
            continue
        gid = target.path_id if target.type.name == "GameObject" else tree(target).get("m_GameObject", {}).get("m_PathID")
        root = by_go.get(gid)
        if root is None:
            continue
        track_clips = t.get("m_Clips", [])
        end = max(end, *(c["m_Start"] + c["m_Duration"] for c in track_clips), 0)
        kind = component_name(track, files)
        if kind == "ActivationTrack" and track_clips:
            gates[root] = [(c["m_Start"], c["m_Start"] + c["m_Duration"]) for c in track_clips]
        elif kind == "AnimationTrack":
            mapping = {0: root}
            def visit(node, path):
                for child in nodes[node]["children"]:
                    part = nodes[child]["raw"]
                    rel = f"{path}/{part}" if path else part
                    h = crc(rel)
                    mapping[h] = child if h not in mapping else None
                    visit(child, rel)
            visit(root, "")
            parts = []
            infinite = resolve(track, t.get("m_InfiniteClip"), files)
            if infinite:
                animation = Clip(tree(infinite))
                end = max(end, animation.duration)
                parts.append((animation, 0.0, math.inf, 0.0, 1.0))
            for clip in track_clips:
                playable = resolve(track, clip.get("m_Asset"), files)
                anim = resolve(playable, tree(playable).get("m_Clip"), files) if playable else None
                if anim and anim.type.name == "AnimationClip":
                    parts.append((Clip(tree(anim)), clip["m_Start"], clip["m_Start"] + clip["m_Duration"],
                                  clip.get("m_ClipIn", 0), clip.get("m_TimeScale", 1)))
            for animation, start, stop, offset, rate in parts:
                matches = []
                index = 0
                for b in animation.bindings:
                    attr, kind = b["attribute"], b["typeID"]
                    width = {1: 3, 2: 4, 3: 3, 4: 3}.get(attr, 1) if kind == 4 else 1
                    node = mapping.get(b["path"])
                    if node is not None and kind == 4 and attr in (1, 2, 3, 4):
                        matches.append((node, attr, index, width))
                    elif node is not None and kind == 1 and attr == crc("m_IsActive"):
                        matches.append((node, 5, index, 1))
                    index += width
                if matches:
                    clips.append((animation, start, stop, offset, rate, matches))
    if not (clips or gates) or not 0 < end <= 30:
        return {}, 0, None
    count = math.ceil(end * FPS)
    moving = {node for _, _, _, _, _, matches in clips for node, attr, _, _ in matches if attr != 5}
    ancestor_chains = {}
    for node in slots_by_node:
        ancestors = []
        parent = node
        while parent is not None:
            ancestors.append(parent)
            parent = nodes[parent]["parent"]
        ancestor_chains[node] = ancestors
    values = defaultdict(lambda: defaultdict(list))
    visible = defaultdict(list)
    for frame in range(count + 1):
        at = frame / FPS
        pose = {n: setup[n].copy() for n in moving}
        active = initial_active.copy()
        for node, spans in gates.items():
            active[node] = any(start <= at < stop - 1e-7 for start, stop in spans)
        for animation, start, stop, offset, rate, matches in clips:
            if not start - 1e-7 <= at <= stop + 1e-7:
                continue
            time = max(0, min(animation.duration, (at - start) * rate + offset))
            for node, attr, index, width in matches:
                v = [animation.value(index + i, time) for i in range(width)]
                if any(x is None or not math.isfinite(x) for x in v):
                    continue
                if attr == 1:
                    pose[node][0:2] = [v[0] * UNIT, v[1] * UNIT]
                elif attr == 2:
                    pose[node][2] = angle(v)
                elif attr == 3:
                    pose[node][3:5] = v[:2]
                elif attr == 4:
                    pose[node][2] = v[2]
                else:
                    active[node] = v[0] > 0.5
        for node, p in pose.items():
            name = nodes[node]["name"]
            rest = setup[node]
            xy = tuple(roundf(p[i] - rest[i]) for i in (0, 1))
            rotation = roundf((p[2] - rest[2] + 180) % 360 - 180)
            scale = tuple(roundf(p[i] / rest[i]) if rest[i] else 1 for i in (3, 4))
            values[name]["translate"].append(xy)
            values[name]["rotate"].append((rotation,))
            values[name]["scale"].append(scale)
        for node, ancestors in ancestor_chains.items():
            for slot, attachment in slots_by_node[node]:
                name = attachment if all(active[n] for n in ancestors) else None
                if not visible[slot] or visible[slot][-1]["name"] != name:
                    visible[slot].append({"time": roundf(at), "name": name})

    visible_slots = {slot for slot, frames in visible.items() if any(frame["name"] is not None for frame in frames)}
    if not visible_slots:
        return {}, 0, False
    relevant = set()
    for node, node_slots in slots_by_node.items():
        if not any(slot in visible_slots for slot, _ in node_slots):
            continue
        relevant.update(ancestor_chains[node])
        for weighted in weighted_nodes.get(node, []):
            while weighted in nodes:
                relevant.add(weighted)
                weighted = nodes[weighted]["parent"]
    relevant_names = {nodes[n]["name"] for n in relevant}
    bones = {}
    for bone, channels in values.items():
        if bone not in relevant_names:
            continue
        out = {}
        for channel, frames in channels.items():
            default = (1, 1) if channel == "scale" else (0,) if channel == "rotate" else (0, 0)
            if all(frame == default for frame in frames):
                continue
            fields = ("value",) if channel == "rotate" else ("x", "y")
            out[channel] = [
                {"time": roundf(i / FPS), **dict(zip(fields, frame))}
                for i, frame in enumerate(frames)
                if i in (0, count) or frame != frames[i - 1] or frame != frames[i + 1]
            ]
        if out:
            bones[bone] = out
    slots = {slot: {"attachment": frames} for slot, frames in visible.items() if len(frames) > 1}
    if not (bones or slots):
        return {}, 0, True
    # Preserve the Timeline's end even when the final frames only show VFX.
    bones["__root__"] = {"translate": [{"time": roundf(count / FPS), "x": 0, "y": 0}]}
    return {"Timeline_二维": {"bones": bones, "slots": slots}}, count, True


def prefab_preview(bundle, objects, files, texture_pages, sizes):
    nodes, by_go = {}, {}
    for o in objects:
        if o.type.name not in ("Transform", "RectTransform"):
            continue
        d = tree(o)
        go = resolve(o, d.get("m_GameObject"), files)
        if not go:
            continue
        raw = go.peek_name() or "transform"
        nodes[o.path_id] = {"raw": raw, "transform": d, "go": tree(go), "children": []}
        by_go[go.path_id] = o.path_id
    if not nodes:
        return None
    for node, info in nodes.items():
        p = info["transform"].get("m_Father", {}).get("m_PathID")
        info["parent"] = p if p in nodes and p != node else None
        if info["parent"] is not None:
            nodes[p]["children"].append(node)
    bones = [{"name": "__root__"}]
    order, setup, initial = {}, {}, {}
    used_names = {"__root__"}
    def append(node):
        info = nodes[node]
        name = safe(info["raw"])
        if name in used_names:
            name += f"_{len(bones)}"
        used_names.add(name)
        info["name"] = name
        order[node] = len(bones)
        setup[node] = bone_local(info["transform"])
        initial[node] = bool(info["go"].get("m_IsActive", True))
        parent = nodes[info["parent"]]["name"] if info["parent"] is not None else "__root__"
        x, y, a, sx, sy = setup[node]
        # Spine scale timeline is relative to the setup scale.
        sx, sy = (sx if abs(sx) > 1e-6 else 1), (sy if abs(sy) > 1e-6 else 1)
        setup[node][3:5] = [sx, sy]
        bones.append({"name": name, "parent": parent, "x": roundf(x), "y": roundf(y),
                      "rotation": roundf(a), "scaleX": roundf(sx), "scaleY": roundf(sy)})
        for child in info["children"]:
            append(child)
    for node, info in nodes.items():
        if info["parent"] is None:
            append(node)
    skin_nodes = {}
    for o in objects:
        if o.type.name == "MonoBehaviour" and component_name(o, files) == "SpriteSkin":
            s = tree(o)
            skin_nodes[s["m_GameObject"]["m_PathID"]] = s.get("m_BoneTransforms", [])
    sprites = []
    weighted_nodes = {}
    for o in objects:
        if o.type.name != "SpriteRenderer":
            continue
        renderer = tree(o)
        go = renderer["m_GameObject"]["m_PathID"]
        node = by_go.get(go)
        sprite = resolve(o, renderer.get("m_Sprite"), files)
        if node not in order or not sprite:
            continue
        s = tree(sprite)
        rd = s["m_RD"]
        owner = sprite
        atlas = resolve(sprite, s.get("m_SpriteAtlas"), files)
        if atlas:
            records = tree(atlas).get("m_RenderDataMap", [])
            if isinstance(records, dict):
                records = records.items()
            for entry in records:
                pair = (entry["first"], entry["second"]) if isinstance(entry, dict) else entry
                if pair[0] == s.get("m_RenderDataKey"):
                    rd, owner = pair[1], atlas
                    break
        texture = resolve(owner, rd.get("texture"), files)
        page = texture_pages.get(key(texture)) if texture else None
        if page is None or (rd.get("settingsRaw", 0) & 1 and (rd.get("settingsRaw", 0) >> 2) & 15):
            continue
        rect = rd.get("textureRect") or s["m_Rect"]
        x, y, w, h = (round(rect[k]) for k in ("x", "y", "width", "height"))
        if w <= 0 or h <= 0 or x < 0 or y < 0 or x+w > sizes[page][0] or y+h > sizes[page][1]:
            continue
        original = s["m_Rect"]
        ow, oh = max(w, round(original["width"])), max(h, round(original["height"]))
        off = rd.get("textureRectOffset", {})
        ox, oy = round(off.get("x", 0)), round(off.get("y", 0))
        if ox < 0 or oy < 0 or ox+w > ow or oy+h > oh:
            ox, oy, ow, oh = 0, 0, w, h
        region = (page, x, sizes[page][1]-y-h, w, h, ox, oy, ow, oh)
        name = safe(s.get("m_Name", "sprite"))
        path = f"{name}_{len(sprites)+1}"
        ppu = s.get("m_PixelsToUnits") or UNIT
        pivot = s.get("m_Pivot") or {"x": 0.5, "y": 0.5}
        factor = UNIT / ppu
        attachment = {"path": path, "width": ow*factor, "height": oh*factor,
                      "x": roundf((0.5-pivot["x"])*ow*factor), "y": roundf((0.5-pivot["y"])*oh*factor),
                      "scaleX": -1 if renderer.get("m_FlipX") else 1, "scaleY": -1 if renderer.get("m_FlipY") else 1}
        weighted = False
        try:
            data = sprite.read().m_RD
            mesh = MeshHandler(data, sprite.version)
            mesh.process()
            indices = [j for sub in mesh.get_triangles() for tri in sub for j in tri]
            uv, vertices = [], []
            binding = skin_nodes.get(go, [])
            weighted = bool(binding and mesh.m_BoneIndices and mesh.m_BoneWeights and data.m_Bindpose)
            if not indices or not mesh.m_Vertices or max(indices) >= len(mesh.m_Vertices):
                raise ValueError("invalid triangle list")
            for i, v in enumerate(mesh.m_Vertices):
                ux, uy = v[0] * ppu / ow + pivot["x"], 1 - (v[1] * ppu / oh + pivot["y"])
                uv.extend([roundf(ux), roundf(uy)])
                if weighted:
                    weights = []
                    for j, weight in zip(mesh.m_BoneIndices[i], mesh.m_BoneWeights[i]):
                        if weight <= 1e-6:
                            continue
                        b = binding[j]["m_PathID"]
                        m = data.m_Bindpose[j]
                        vx = (m.e00*v[0]+m.e01*v[1]+m.e02*v[2]+m.e03) * UNIT
                        vy = (m.e10*v[0]+m.e11*v[1]+m.e12*v[2]+m.e13) * UNIT
                        weights.append([order[b], roundf(vx), roundf(vy), weight])
                    if not weights:
                        raise ValueError("empty weights")
                    total = sum(w[3] for w in weights)
                    vertices.append(len(weights))
                    for idx, vx, vy, weight in weights:
                        vertices.extend([idx, vx, vy, roundf(weight/total)])
                else:
                    vertices.extend([roundf(v[0]*UNIT), roundf(v[1]*UNIT)])
            attachment = {"type": "mesh", "path": path, "uvs": uv, "vertices": vertices,
                          "triangles": indices, "width": ow*factor, "height": oh*factor}
        except (AttributeError, KeyError, ValueError, AssertionError, IndexError, TypeError):
            weighted = False
        color = renderer.get("m_Color", {})
        tint = "".join(f"{max(0,min(255,round(color.get(k,1)*255))):02x}" for k in "rgba")
        if not renderer.get("m_Enabled", True):
            continue
        if weighted:
            weighted_nodes[node] = [p["m_PathID"] for p in skin_nodes[go]]
        sprites.append((renderer.get("m_SortingLayer", 0), renderer.get("m_SortingOrder", 0), order[node],
                        node, name, path, attachment, tint, region, weighted))
    if not sprites:
        return None
    slots, attachments, regions, slots_by_node = [], {}, {}, defaultdict(list)
    for _, _, _, node, name, path, attachment, color, region, weighted in sorted(sprites, key=lambda r:r[:3]):
        slot = f"{name}_{len(slots)+1}"
        visible, n = True, node
        while n is not None:
            visible &= initial[n]
            n = nodes[n]["parent"]
        row = {"name": slot, "bone": nodes[node]["name"], "color": color}
        if visible:
            row["attachment"] = name
        slots.append(row)
        attachments[slot] = {name: attachment}
        regions[path] = region
        slots_by_node[node].append((slot, name))
    anim, frames, shown = {}, 0, None
    director = next((o for o in objects if o.type.name == "PlayableDirector"), None)
    if director:
        anim, frames, shown = timeline_animation(director, nodes, by_go, files, setup, slots_by_node, weighted_nodes, initial)
    if shown is False or (shown is None and not any(slot.get("attachment") for slot in slots)):
        return None
    return skeleton(bundle, bones, slots, attachments, anim), regions, len(slots), sum(r[-1] for r in sprites), frames


def scan(index):
    result = defaultdict(dict)
    for b in index["Bundles"]:
        for path in b["Containers"]:
            m = EFFECT.search(path)
            if m:
                result[int(m[1])][(Path(b["Path"]).name, path)] = m[2]
    for cid, hits in sorted(result.items()):
        groups = {"Prefabs": [], "Timelines": [], "Other": []}
        for (bundle, logical), tail in hits.items():
            if tail.endswith(".prefab") and re.match(rf"ef0*{cid}(?!\d)", PurePosixPath(tail).name, re.I):
                category = "Prefabs"
            elif tail.endswith(".playable"):
                category = "Timelines"
            else:
                category = "Other"
            groups[category].append({"Bundle": bundle, "LogicalPath": logical})
        if groups["Prefabs"] or groups["Timelines"]:
            yield {"CardId": cid, "Name": index["ChineseNames"].get(str(cid), f"卡号 {cid}"), **groups}


def native_spine(entry, loaded, texture_pages, content):
    by_path = {r["LogicalPath"].lower(): r["Bundle"] for r in entry["Other"]}
    versions = []
    for path, bundle in by_path.items():
        if not path.endswith((".atlas.txt", ".atlas")):
            continue
        directory = PurePosixPath(path).parent
        quality = directory.parent
        atlases = [o for o in loaded.get(bundle, []) if o.type.name == "TextAsset" and ".atlas" in o.peek_name().lower()]
        candidates = [bid for p, bid in by_path.items() if PurePosixPath(p).parent == quality and p.endswith(".json")]
        if len(atlases) != 1 or len(candidates) != 1:
            continue
        sk = next((o for o in loaded.get(candidates[0], []) if o.type.name == "TextAsset"), None)
        if not sk:
            continue
        raw = sk.read().m_Script
        document = json.loads(raw)
        if not document.get("skeleton", {}).get("spine") or not document.get("animations"):
            continue
        atlas = atlases[0].read().m_Script
        names = re.findall(r"(?im)^[^:\r\n]+\.png\s*$", atlas)
        mapped = {}
        for n in names:
            n = n.strip()
            page_bundle = by_path.get(str(directory/n).lower())
            pages = [texture_pages.get(key(o)) for o in loaded.get(page_bundle, []) if o.type.name == "Texture2D" and o.peek_name().casefold() == Path(n).stem.casefold()]
            if len(pages) == 1 and pages[0]:
                mapped[n] = pages[0]
        if not names or len(mapped) != len(names):
            continue
        for old, new in mapped.items():
            atlas = re.sub(rf"(?m)^{re.escape(old)}(?=\r?$)", lambda _: new, atlas)
        label = f"原Spine-{safe(quality.name)}-{bundle}"
        content[label+".json"] = raw.encode("utf-8")
        content[label+".atlas"] = atlas.encode("utf-8")
        versions.append({"Model":label+".json","Sprites":0,"WeightedMeshes":0,"Frames":0,"NativeSpine":True})
    return versions


def referenced_textures(objects, files):
    def textures(owner, links):
        for link in links:
            target = resolve(owner, link, files)
            if target and target.type.name == "Texture2D":
                yield key(target)

    def material(obj):
        if obj and obj.type.name == "Material":
            links = [entry["m_Texture"] for _, entry in tree(obj)["m_SavedProperties"]["m_TexEnvs"]]
            yield from textures(obj, links)

    found = set()
    for obj in objects:
        try:
            kind = obj.type.name
            if kind == "Material":
                found.update(material(obj))
            elif kind in ("SpriteRenderer", "MeshRenderer", "SkinnedMeshRenderer", "ParticleSystemRenderer"):
                data = tree(obj)
                for link in data.get("m_Materials", []):
                    found.update(material(resolve(obj, link, files)))
                sprite = resolve(obj, data.get("m_Sprite"), files)
                if sprite and sprite.type.name == "Sprite":
                    render = tree(sprite).get("m_RD", {})
                    found.update(textures(sprite, [render.get("texture"), render.get("alphaTexture")]))
        except (AttributeError, KeyError, TypeError, ValueError, IndexError, RuntimeError, EOFError):
            continue
    return found


def dependency_routes(owned, loaded):
    routes = {bid: () for bid in sorted(owned)}
    queue = list(routes)
    for bid in queue:
        for obj in loaded.get(bid, []):
            if obj.type.name != "AssetBundle":
                continue
            for name in obj.read().m_Dependencies:
                child = PurePosixPath(name.replace("\\", "/")).name
                if child in loaded and child not in routes and len(routes[bid]) < 8:
                    routes[child] = (*routes[bid], bid)
                    queue.append(child)
    return routes


def extract_card(entry, bundles, folder, archive_path=None):
    cid, name = entry["CardId"], entry["Name"]
    owned = {r["Bundle"] for k in ("Prefabs","Timelines","Other") for r in entry[k]}
    loaded = {}
    for b in bundles.dependencies(sorted(owned)):
        try:
            loaded[b] = bundles.open(b)
        except Exception:
            if b in owned:
                raise
    files = {o.assets_file.name:o.assets_file for objects in loaded.values() for o in objects}
    pages, sizes, pngs, texture_pages, signatures = [], {}, {}, {}, {}
    references = referenced_textures((obj for bid in owned for obj in loaded.get(bid, [])), files)
    routes = dependency_routes(owned, loaded)
    for bid in sorted(routes):
        path = bundles.paths.get(bid)
        if path is None or path.parent.parent.name != "0000":
            continue
        via = [bundles.paths[parent] for parent in routes[bid]]
        if any(parent.parent.parent.name != "0000" for parent in via):
            continue
        for o in loaded.get(bid, []):
            if o.type.name != "Texture2D" or (bid not in owned and key(o) not in references):
                continue
            t = o.read()
            fmt = int(t.m_TextureFormat)
            if fmt not in SUPPORTED or min(t.m_Width, t.m_Height) < 1 or max(t.m_Width, t.m_Height) > 8192:
                continue
            signature = (t.m_Name, fmt, t.m_MipCount, t.m_Width, t.m_Height, hashlib.sha256(t.get_image_data()).digest())
            page = signatures.get(signature)
            if page is None:
                base = safe(t.m_Name)
                filename = base + ".png"
                if filename.casefold() in {p.casefold() for p in sizes}:
                    filename = f"{base}-{bid}-{abs(o.path_id)}.png"
                image = t.image.convert("RGBA")
                stream = io.BytesIO()
                image.save(stream, "PNG", compress_level=3)
                png = stream.getvalue()
                pngs[filename] = png
                sizes[filename] = (t.m_Width, t.m_Height)
                page = {"File": filename, "OriginalPngSha256": hashlib.sha256(png).hexdigest(), "Width": t.m_Width,
                        "Height": t.m_Height, "Format": fmt, "Mips": t.m_MipCount, "Targets": []}
                signatures[signature] = page
                pages.append(page)
            texture_pages[key(o)] = page["File"]
            target = {"Bundle":f"0000/{path.parent.name}/{bid}", "BundleSha256":bundles.digest(bid),
                      "FileIndex":file_index(o), "PathId":o.path_id, "Name":t.m_Name}
            if via:
                target["Via"] = [{"Bundle":f"0000/{parent.parent.name}/{parent.name}", "BundleSha256":bundles.digest(parent.name)}
                                 for parent in via]
            page["Targets"].append(target)
    if not pngs:
        return {"CardId": cid, "Name": name, "Status": "无支持格式的可编辑贴图", "Versions": [], "Pages": 0}

    content = dict(pngs)
    content["效果贴图映射.json"] = json_bytes({"Format":"md-unity-effect-textures-v1","CardId":cid,"Name":name,"Pages":pages})
    content["游戏资源索引.json"] = json_bytes(entry)
    sheet, layout = sheet_preview(sizes)
    content["贴图一览.json"] = json_bytes(sheet)
    content["贴图一览.atlas"] = atlas_bytes(layout, sizes)
    versions = []
    errors = []
    for prefab in entry["Prefabs"]:
        bid, logical = prefab["Bundle"], prefab["LogicalPath"]
        label = safe(PurePosixPath(logical).stem) + "-" + bid
        try:
            converted = prefab_preview(bid, loaded.get(bid, []), files, texture_pages, sizes)
            if converted is None:
                continue
            doc, regions, sprites, meshes, frames = converted
            content[label+".json"] = json_bytes(doc)
            content[label+".atlas"] = atlas_bytes(regions, sizes)
            versions.append({"Model":label+".json","Sprites":sprites,"WeightedMeshes":meshes,"Frames":frames})
        except Exception as ex:
            errors.append(f"{label}: {type(ex).__name__} {ex}")
    try:
        versions.extend(native_spine(entry, loaded, texture_pages, content))
    except Exception as ex:
        errors.append(f"Spine: {type(ex).__name__} {ex}")
    status = "原生 Spine 与二维预览" if any(v.get("NativeSpine") for v in versions) else (
        "含二维时间轴片段" if any(v["Frames"] for v in versions) else "二维拆件参考" if versions else "贴图参考")
    readme = [f"# {name}〔{cid}〕", "", f"预览级别：{status}。", "",
              "Rigby2D 中打开同目录的 JSON。贴图一览显示可编辑的原图集页。",
              "角色项目保留可读取的二维网格与权重。动作仅转换可映射的骨骼变换与显隐。Unity 粒子、3D、材质和音效由游戏原资源继续播放。",
              "回包可直接编辑此文件夹内的原有 PNG，保留文件名、大小、透明度和原图集坐标。保留「效果贴图映射.json」。",
              "也可在 Rigby2D 中导出「JSON + Atlas」，放到「Rigby2D导出」子文件夹。保持 atlas 与原模型同名，并保留图片资源名及部件原画布尺寸。",
              "在助手首页选择解压后的此文件夹，点击「打包效果替换文件」。Unity 原动作继续使用游戏原文件。",
              "", *[f"- {v['Model']} · 原生 Spine" if v.get("NativeSpine") else
                    f"- {v['Model']} · {v['Sprites']} 个 Sprite · {v['WeightedMeshes']} 个带权重网格 · {v['Frames']} 个动作采样间隔" for v in versions]]
    if errors:
        readme.extend(["","转换记录",*errors])
    content["使用说明.md"] = ("\n".join(readme) + "\n").encode()
    target = archive_path or (folder / f"{cid}-{safe(name)}-效果素材.zip")
    staged = target.with_name(target.name + ".tmp")
    try:
        with zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED, compresslevel=4) as z:
            for filename, data in content.items():
                z.writestr(filename, data, compress_type=zipfile.ZIP_STORED if filename.endswith(".png") else zipfile.ZIP_DEFLATED)
        staged.replace(target)
    finally:
        staged.unlink(missing_ok=True)
    return {"CardId":cid,"Name":name,"Status":status,"Versions":versions,"Pages":len(pages),"File":target.name,"Errors":errors}


def write_indexes(rows, output):
    (output / "效果素材索引.json").write_bytes(json_bytes(rows))
    with (output / "效果目录.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["卡号","游戏中文名","预览级别","贴图页数","素材包"])
        writer.writerows((r["CardId"],r["Name"],r["Status"],r["Pages"],r.get("File","")) for r in rows)
    table = []
    for r in rows:
        title = html.escape(r["Name"])
        file = r.get("File")
        link = f'<a href="{quote(file)}">下载素材</a>' if file else ""
        table.append(f'<tr><td>{r["CardId"]}</td><td>{title}</td><td>{r["Status"]}</td><td>{r["Pages"]}</td><td>{link}</td></tr>')
    doc = ('<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>卡片效果动画目录</title>'
           '<style>body{font:16px system-ui;max-width:1050px;margin:40px auto;padding:0 20px}table{border-collapse:collapse;width:100%}'
           'td,th{padding:9px;text-align:left;border-bottom:1px solid #ccc}</style><h1>卡片效果动画目录</h1>'
           '<p>解压各卡 ZIP。二维动作预览不包括原版 3D、粒子和材质。</p><table>'
           '<tr><th>卡号</th><th>游戏中文名</th><th>预览级别</th><th>贴图</th><th>素材包</th></tr>' + "".join(table) + "</table>")
    (output / "效果目录.html").write_text(doc, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--index", type=Path, required=True)
    destination = parser.add_mutually_exclusive_group(required=True)
    destination.add_argument("--out", type=Path)
    destination.add_argument("--zip", type=Path, help="Write exactly one effect package, with --only CARD_ID")
    parser.add_argument("--catalog", type=Path)
    parser.add_argument("--only", type=int, nargs="+")
    parser.add_argument("--game-root", type=Path, help="Map indexed game paths to the current installation")
    parser.add_argument("--account", help="LocalData account folder in the current installation")
    args = parser.parse_args()
    if bool(args.game_root) != bool(args.account):
        parser.error("--game-root and --account are used together")
    if args.account and not re.fullmatch(r"[0-9a-zA-Z_-]+", args.account):
        parser.error("无效的游戏资源目录名")
    index = json.loads(args.index.read_text("utf-8-sig"))
    entries = list(scan(index))
    if args.catalog:
        args.catalog.write_bytes(json_bytes(entries))
    bundles = Bundles(index, args.game_root, args.account)
    if args.zip:
        if not args.only or len(args.only) != 1:
            parser.error("--zip requires exactly one card ID via --only")
        entry = next((e for e in entries if e["CardId"] == args.only[0]), None)
        if entry is None:
            parser.exit(2, "素材索引中没有此卡的效果动画，请先更新目录。\n")
        expected = {r["Bundle"] for group in ("Prefabs", "Timelines") for r in entry[group]}
        if expected - bundles.paths.keys():
            parser.exit(2, "当前账号缺少此卡的预制体或时间轴，请先在游戏里下载相关资源并刷新目录。\n")
        result = extract_card(entry, bundles, args.zip.parent, args.zip)
        if not result["Pages"]:
            parser.exit(2, "当前游戏目录中没有找到可提取的效果贴图。\n")
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return
    args.out.mkdir(parents=True, exist_ok=True)
    index_path = args.out / "效果素材索引.json"
    previous = json.loads(index_path.read_text("utf-8-sig")) if args.only and index_path.is_file() else []
    rows = {row["CardId"]: row for row in previous}
    for entry in entries:
        if args.only and entry["CardId"] not in args.only:
            continue
        try:
            result = extract_card(entry, bundles, args.out)
        except Exception as ex:
            result = {"CardId":entry["CardId"],"Name":entry["Name"],"Status":"提取失败","Pages":0,"Versions":[],
                      "Errors":[type(ex).__name__ + ": " + str(ex)]}
        rows[result["CardId"]] = result
        print(result["CardId"], result["Name"], result["Status"], result["Pages"],
              [(r["Sprites"],r["WeightedMeshes"],r["Frames"]) for r in result["Versions"]], result.get("Errors",[]), flush=True)
        bundles.open.cache_clear()
    write_indexes([rows[key] for key in sorted(rows)], args.out)


if __name__ == "__main__":
    main()
