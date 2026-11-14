"""Find the Spine data reached by a card's models and animation timeline."""
import json
from pathlib import Path
import re
import sys

from extract_effects import Bundles, file_index, resolve


def discover(index, game, account, card, region, output, share_bindings=False):
    bundles = Bundles({"Bundles": []}, game, account)
    prefix = f"/monstercutin/{region}/p{card}/"
    roots = {}
    for bundle in index["Bundles"]:
        for container in bundle["Containers"]:
            if prefix not in container.lower():
                continue
            tail = container.lower().split(prefix, 1)[1]
            if re.fullmatch(r"(sd|highend_hd)/.*\.prefab|[^/]+\.playable", tail):
                key = Path(bundle["Path"]).name
                roots[key] = tail

    owners = {}
    hashes = {}

    def path(key):
        if not re.fullmatch(r"[a-f0-9]{8}", key):
            raise ValueError("召唤依赖文件名无效：" + key)
        return game / "LocalData" / account / "0000" / key[:2] / key

    def opened(key):
        live = path(key)
        if key not in bundles.paths:
            bundles.paths[key] = live
            hashes[key] = bundles.digest(key)
        objs = bundles.open(key)
        for obj in objs:
            owners[obj.assets_file] = key
        return objs

    def dereference(owner, pointer):
        if not pointer or not pointer.get("m_PathID"):
            return None
        owner_file = owner.assets_file
        if not pointer.get("m_FileID"):
            target = resolve(owner, pointer, {})
        else:
            index = pointer["m_FileID"] - 1
            if not 0 <= index < len(owner_file.externals):
                raise ValueError("召唤资源的 CAB 引用无效。")
            cab = owner_file.externals[index].name
            current = owners[owner_file]
            pending = [current]
            searched = set()
            target = None
            while pending and target is None:
                key = pending.pop(0)
                if key in searched or (key not in bundles.paths and not path(key).is_file()):
                    continue
                searched.add(key)
                objs = opened(key)
                files = {o.assets_file.name: o.assets_file for o in objs}
                if cab in files:
                    target = resolve(owner, pointer, files)
                    break
                for obj in objs:
                    if obj.type.name == "AssetBundle":
                        pending.extend(Path(dep.replace("\\", "/")).name for dep in obj.read().m_Dependencies)
        if target is None:
            raise ValueError("无法读取召唤动画的引用资源（" + owners[owner_file] + "）。请更新目录并补齐游戏资源。")
        return target

    def locator(obj):
        return {"BundleId": owners[obj.assets_file], "FileIndex": file_index(obj), "PathId": obj.path_id,
                "Name": obj.peek_name(), "Type": obj.type.name}

    models = {}
    prefab_bindings = {}
    timeline_skeletons = {}
    binding_files = []

    def skeleton(obj, quality, animation=None):
        data = obj.read_typetree()
        if not data.get("skeletonJSON") or not data.get("atlasAssets"):
            raise ValueError("召唤模型的骨架资源缺少 JSON 或 atlas。")
        json_obj = dereference(obj, data["skeletonJSON"])
        if json_obj.type.name != "TextAsset" or not re.match(rf"^p{card}(?!\d)", json_obj.peek_name(), re.I):
            return False
        for atlas_link in data["atlasAssets"]:
            atlas_obj = dereference(obj, atlas_link)
            atlas_data = atlas_obj.read_typetree()
            text = dereference(atlas_obj, atlas_data["atlasFile"])
            pages = []
            for mat_link in atlas_data["materials"]:
                mat = dereference(atlas_obj, mat_link)
                saved = mat.read_typetree()["m_SavedProperties"]["m_TexEnvs"]
                main = next((value for name, value in saved if name == "_MainTex"), None)
                if main is not None:
                    page = dereference(mat, main["m_Texture"])
                    if page is not None and page.type.name == "Texture2D":
                        pages.append(page)
            if not pages:
                raise ValueError("召唤图集缺少可定位的贴图材质。")
            source = (locator(json_obj), locator(text), [locator(page) for page in pages])
            ident = (json_obj.assets_file.name, json_obj.path_id, text.assets_file.name, text.path_id,
                     tuple((page.assets_file.name, page.path_id) for page in pages))
            if ident not in models:
                number = len(models)
                skeleton_file, atlas_file = f"{number}.json", f"{number}.atlas"
                (output / skeleton_file).write_text(json_obj.read().m_Script, encoding="utf-8", newline="")
                (output / atlas_file).write_text(text.read().m_Script, encoding="utf-8", newline="")
                dimensions = [(texture.m_Width, texture.m_Height) for texture in (page.read() for page in pages)]
                models[ident] = {"Skeleton": source[0], "Atlas": source[1], "Pages": source[2],
                                 "SkeletonFile": skeleton_file, "AtlasFile": atlas_file,
                                 "Dimensions": dimensions, "Qualities": [], "Animations": []}
            model = models[ident]
            if quality and quality not in model["Qualities"]:
                model["Qualities"].append(quality)
            if animation and animation not in model["Animations"]:
                model["Animations"].append(animation)
        return True

    for key, tail in roots.items():
        if not path(key).is_file():
            raise ValueError("当前账号缺少召唤文件：" + key + "。请在游戏中载入该卡对应画质的动画。")
        objs = opened(key)
        for obj in objs:
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                data = obj.read_typetree()
            except (ValueError, TypeError, KeyError):
                continue
            if tail.endswith(".prefab") and data.get("skeletonDataAsset", {}).get("m_PathID"):
                target = dereference(obj, data["skeletonDataAsset"])
                quality = tail.split("/", 1)[0]
                if skeleton(target, quality):
                    prefab_bindings.setdefault(key, []).append((obj, target, quality))
            if tail.endswith(".playable") and isinstance(data.get("template"), dict):
                link = data["template"].get("animationReference")
                if not link or not link.get("m_PathID"):
                    continue
                ref = dereference(obj, link)
                ref_data = ref.read_typetree()
                if ref_data.get("skeletonDataAsset"):
                    target = dereference(ref, ref_data["skeletonDataAsset"])
                    animation = ref_data.get("animationName")
                    if skeleton(target, None, animation) and animation:
                        timeline_skeletons.setdefault(key, []).append((target, ref, animation))

    def same_object(left, right):
        return (owners[left.assets_file], left.assets_file.name, left.path_id) == (
            owners[right.assets_file], right.assets_file.name, right.path_id)

    available_qualities = {quality for entries in prefab_bindings.values() for _, _, quality in entries}
    if share_bindings and {"sd", "highend_hd"} <= available_qualities:
        from summon_bindings import bind_prefab
        for key, entries in prefab_bindings.items():
            if len(entries) != 1:
                raise ValueError("该召唤模型含多个 Spine 播放器，无法自动统一形变轨道。")
            component, old_skeleton, quality = entries[0]
            prefab = opened(key)
            directors = [obj for obj in prefab if obj.type.name == "PlayableDirector"
                         and obj.assets_file is component.assets_file
                         and any(row["value"] == {"m_FileID": 0, "m_PathID": component.path_id}
                                 for row in obj.read_typetree()["m_SceneBindings"])]
            if len(directors) != 1:
                raise ValueError("无法确认该画质的召唤时间轴绑定。请更新动画目录。")
            director = directors[0]
            timeline = dereference(director, director.read_typetree()["m_PlayableAsset"])
            candidates = timeline_skeletons.get(owners[timeline.assets_file], [])
            if not candidates:
                raise ValueError("找不到召唤时间轴使用的 Spine 动画引用。请更新动画目录。")
            if all(same_object(old_skeleton, item[0]) for item in candidates):
                continue
            target, reference, _ = candidates[0]
            if not all(same_object(target, item[0]) for item in candidates):
                raise ValueError("召唤时间轴引用了不同的骨架对象，无法自动合并形变轨道。")
            if not any(same_object(target, other) for entries in prefab_bindings.values()
                       for _, other, other_quality in entries if other_quality != quality):
                raise ValueError("时间轴使用的骨架未在另一画质中找到，无法安全共用形变轨道。")
            materials = []
            for link in target.read_typetree()["atlasAssets"]:
                atlas = dereference(target, link)
                for material in atlas.read_typetree()["materials"]:
                    material_obj = dereference(atlas, material)
                    if not any(same_object(material_obj, existing) for existing in materials):
                        materials.append(material_obj)
            previous_materials = []
            for link in old_skeleton.read_typetree()["atlasAssets"]:
                atlas = dereference(old_skeleton, link)
                previous_materials.extend(dereference(atlas, material) for material in atlas.read_typetree()["materials"])
            shared = bind_prefab(path(key).read_bytes(), component, target, reference, materials,
                                 previous_materials, owners, dereference)
            (output / (key + ".bindings")).write_bytes(shared)
            binding_files.append(key)
            for model in models.values():
                if quality in model["Qualities"]:
                    model["Qualities"].remove(quality)
            for candidate, _, animation in candidates:
                skeleton(candidate, quality, animation)

    if roots and not models:
        raise ValueError("未找到该卡实际引用的 Spine 召唤资源。请更新动画目录。")
    active = [model for model in models.values() if model["Qualities"] or model["Animations"]]
    used = set(binding_files)
    for model in active:
        used.update(asset["BundleId"] for asset in [model["Skeleton"], model["Atlas"], *model["Pages"]])
        if len(model["Qualities"]) > 1:
            used.update(key for key, tail in roots.items() if tail.endswith(".prefab"))
    return {"Models": active, "Hashes": {key: hashes[key] for key in sorted(used)}, "Bindings": binding_files}


if __name__ == "__main__":
    index_file, game, account, card, region, output, *options = sys.argv[1:]
    try:
        folder = Path(output)
        index = json.loads(Path(index_file).read_text(encoding="utf-8-sig"))
        result = discover(index, Path(game), account, int(card), region, folder, "share-bindings" in options)
        (folder / "dependencies.json").write_text(json.dumps(result), encoding="utf-8")
    except ValueError as error:
        print(error)
        sys.exit(2)
