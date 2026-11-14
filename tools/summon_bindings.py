"""Make a summon renderer and its Timeline use the same Spine skeleton object."""
import copy

from pack_shared_summon import load, pack, ptr, relative, verify_untouched


def bind_prefab(raw, component, target, reference, materials, previous_materials, owners, dereference):
    env, bundle, cab = load(raw)
    renderer_component = cab.objects[component.path_id]
    state = renderer_component.read_typetree()
    descriptor, = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    metadata = descriptor.read_typetree()
    renderers = [obj for obj in env.objects if obj.type.name == "MeshRenderer"
                 and obj.read_typetree()["m_GameObject"] == state["m_GameObject"]]
    if len(renderers) != 1:
        raise ValueError("无法定位召唤骨架对应的材质渲染器。")
    renderer, = renderers
    render = renderer.read_typetree()
    if not materials or not render["m_Materials"]:
        raise ValueError("共用召唤骨架缺少可用材质。")

    def identity(obj):
        return obj.assets_file.name, obj.path_id

    def reference_to(obj, owner, known_links, replace_index=0):
        if obj.assets_file.name == cab.name:
            return ptr(0, obj.path_id)
        index = next((i for i, entry in enumerate(cab.externals, 1) if entry.name == obj.assets_file.name), None)
        if index is None:
            external = next((entry for entry in owner.assets_file.externals
                             if entry.name == obj.assets_file.name), None)
            if external is None:
                external = next((entry for _, entry in known_links if entry.name == obj.assets_file.name), None)
            if external is None:
                raise ValueError("无法确定共用骨架材质的 Unity 引用。")
            if replace_index:
                cab.externals[replace_index-1] = copy.copy(external)
                index = replace_index
            else:
                cab.externals.append(copy.copy(external))
                index = len(cab.externals)
        dependency = relative(owners[obj.assets_file]).removeprefix("0000/")
        if dependency not in metadata["m_Dependencies"]:
            metadata["m_Dependencies"].append(dependency)
        return ptr(index, obj.path_id)

    # Find the original external references through the selected SkeletonDataAsset and its atlas.
    links = []
    for p in target.read_typetree()["atlasAssets"]:
        atlas = dereference(target, p)
        links.extend((atlas, entry) for entry in atlas.assets_file.externals)
    links.extend((target, entry) for entry in target.assets_file.externals)

    old_skeleton = state["skeletonDataAsset"]
    previous_skeleton = dereference(component, old_skeleton)
    old_materials = list(render["m_Materials"])
    scene_renderer = component.assets_file.objects[renderer.path_id]
    previous_ids = {identity(obj) for obj in previous_materials}
    for p in old_materials:
        existing = dereference(scene_renderer, p)
        if existing is None or identity(existing) not in previous_ids:
            raise ValueError("原模型含有独立材质，无法安全切换到时间轴骨架。")
    # The common single-material case uses the exact same file slots as the original prefab.
    # Confirm those slots have no references outside the renderer fields and the preload table.
    replaceable = {}
    old_targets = [(old_skeleton, previous_skeleton)]
    old_targets.extend((pointer, dereference(scene_renderer, pointer)) for pointer in old_materials)
    for old, obj in old_targets:
        index = old["m_FileID"]
        if index and sum(pointer["m_FileID"] == index for pointer, _ in old_targets) == 1:
            replaceable[index] = obj

    def contains_external(value, index):
        if isinstance(value, dict):
            return value.get("m_FileID") == index or any(contains_external(child,index) for child in value.values())
        if isinstance(value, (list, tuple)):
            return any(contains_external(child,index) for child in value)
        return False

    excluded = {component.path_id, renderer.path_id, descriptor.path_id}
    retained_metadata = {name:value for name,value in metadata.items() if name not in ("m_Dependencies", "m_PreloadTable")}
    replaced_pointers = [old_skeleton, *old_materials]
    for index in list(replaceable):
        if (contains_external(retained_metadata,index)
            or contains_external({k:v for k,v in state.items() if k != "skeletonDataAsset"}, index)
            or contains_external({k:v for k,v in render.items() if k != "m_Materials"}, index)
            or any(pointer["m_FileID"] == index and pointer not in replaced_pointers for pointer in metadata["m_PreloadTable"])
            or any(contains_external(item.read_typetree(),index) for item in env.objects if item.path_id not in excluded)):
            del replaceable[index]

    def replace_link(obj, owner, old):
        index = old["m_FileID"] if old is not None else 0
        previous = replaceable.get(index)
        current = reference_to(obj, owner, links, index if previous is not None else 0)
        if previous is not None and current["m_FileID"] == index:
            previous_dep = relative(owners[previous.assets_file]).removeprefix("0000/")
            new_dep = relative(owners[obj.assets_file]).removeprefix("0000/")
            if previous_dep != new_dep and previous_dep in metadata["m_Dependencies"]:
                metadata["m_Dependencies"] = list(dict.fromkeys(
                    new_dep if key == previous_dep else key for key in metadata["m_Dependencies"]))
        return current

    new_skeleton = replace_link(target, reference, old_skeleton)
    new_materials = [replace_link(material,target,old_materials[i] if len(materials)==len(old_materials) else None)
                     for i, material in enumerate(materials)]

    def remap_preloads(previous):
        updated = []
        for pointer in previous:
            if pointer == old_skeleton:
                updated.append(new_skeleton)
            elif pointer in old_materials:
                for replacement in new_materials:
                    if replacement not in updated:
                        updated.append(replacement)
            else:
                updated.append(pointer)
        for required in [new_skeleton, *new_materials]:
            if required not in updated:
                updated.append(required)
        return updated

    # Preserve the span of each preload group and any shared container ranges.
    preloads = metadata["m_PreloadTable"]
    groups = [entry for _, entry in metadata["m_Container"] if entry["preloadSize"] > 0]
    if metadata["m_MainAsset"]["preloadSize"] > 0:
        groups.append(metadata["m_MainAsset"])
    if not groups or any(entry["preloadIndex"] != 0 or entry["preloadSize"] != len(preloads) for entry in groups):
        raise ValueError("召唤预加载表含有不同的资源组，无法安全共享骨架。")
    new_preloads = remap_preloads(preloads)
    metadata["m_PreloadTable"] = new_preloads
    for entry in groups:
        entry["preloadSize"] = len(new_preloads)

    state["skeletonDataAsset"] = new_skeleton
    render["m_Materials"] = new_materials
    renderer_component.save_typetree(state)
    renderer.save_typetree(render)
    descriptor.save_typetree(metadata)
    result = pack(bundle, {})
    verify_untouched(raw, result, {component.path_id, renderer.path_id, descriptor.path_id})

    _, _, checked = load(result)
    got = checked.objects[component.path_id].read_typetree()["skeletonDataAsset"]
    got_materials = checked.objects[renderer.path_id].read_typetree()["m_Materials"]
    got_metadata = checked.objects[descriptor.path_id].read_typetree()
    if got != new_skeleton or got_materials != new_materials or got_metadata != metadata:
        raise ValueError("共用骨架写入后回读不一致。")
    for pointer, original in [(got, target), *zip(got_materials, materials)]:
        index = pointer["m_FileID"]
        name = checked.externals[index-1].name if index else checked.name
        if (name, pointer["m_PathID"]) != identity(original):
            raise ValueError("共用骨架或材质引用回读不一致。")
    return result
