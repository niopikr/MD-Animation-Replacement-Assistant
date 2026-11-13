"""Pack the verified card 14409 TCG summon dependency chain."""
import copy
from hashlib import sha256
import json
import ntpath
from pathlib import Path
import re
import sys
from zipfile import ZIP_STORED, ZipFile

from PIL import Image, ImageChops
import UnityPy
from UnityPy.export.Texture2DConverter import image_to_texture2d
from UnityPy.streams import EndianBinaryReader


SHARED = "294d714a"
TIMELINE = "a1e79578"
SKELETON = -4535421533932665725
TEXTURE = -736984204464174493
ANIMATION = 8964257468671756670
MODELS = {
    "2caa2654": (-2074957049910496476, (-5518257390235027078, 9205077971633838809)),
    "d14c1c69": (5231055798973766054, (7768257522264920227,)),
}


class ResourceError(ValueError):
    pass


def relative(key):
    return f"0000/{key[:2]}/{key}"


def load(raw):
    env = UnityPy.load(raw)
    bundle, = env.files.values()
    cab, = env.assets
    return env, bundle, cab


def raw_entry(entry):
    return bytes(entry.bytes if hasattr(entry, "bytes") else entry.reader.bytes)


def pack(bundle, overrides):
    for name, entry in list(bundle.files.items()):
        if name in overrides:
            data = overrides[name]
        else:
            data = entry.save() if hasattr(entry, "objects") else raw_entry(entry)
        reader = EndianBinaryReader(data)
        reader.flags = entry.flags
        bundle.files[name] = reader
    return bundle.save(packer="lz4")


def named(env, name):
    obj, = [obj for obj in env.objects if obj.type.name == "TextAsset" and obj.peek_name() == name]
    return obj


def ptr(file_index, path_id):
    return {"m_FileID": file_index, "m_PathID": path_id}


def resolves(cab, pointer, external, path_id):
    index = pointer["m_FileID"]
    return 0 < index <= len(cab.externals) and cab.externals[index - 1].name == external and pointer["m_PathID"] == path_id


def uses_local_skeleton(cab, link):
    if link["m_FileID"] != 0:
        return False
    data = cab.objects.get(link["m_PathID"])
    if data is None or data.type.name != "MonoBehaviour":
        return False
    reference = data.read_typetree().get("skeletonJSON")
    if not reference or reference["m_FileID"] != 0:
        return False
    skeleton = cab.objects.get(reference["m_PathID"])
    return skeleton is not None and skeleton.type.name == "TextAsset" and skeleton.peek_name() == "P14409JS"


def material_texture(material):
    entry, = [entry for name, entry in material["m_SavedProperties"]["m_TexEnvs"] if name == "_MainTex"]
    return entry


def verify_untouched(original, changed, edited):
    before, _, _ = load(original)
    after, _, _ = load(changed)
    objects = {obj.path_id: obj.get_raw_data() for obj in after.objects}
    if len(objects) != len(list(before.objects)) or any(
        objects.get(obj.path_id) != obj.get_raw_data() for obj in before.objects if obj.path_id not in edited
    ):
        raise ValueError("Unrelated objects changed")


def edit_model(key, original, external):
    env, bundle, cab = load(original)
    names = [entry.name for entry in cab.externals]
    if external.name not in names:
        cab.externals.append(copy.copy(external))
        names.append(external.name)
    index = names.index(external.name) + 1
    renderer_id, material_ids = MODELS[key]
    renderer = cab.objects[renderer_id]
    data = renderer.read_typetree()
    link = data["skeletonDataAsset"]
    if not uses_local_skeleton(cab, link) and not resolves(cab, link, external.name, SKELETON):
        raise ResourceError(f"召唤模型 {key} 的骨架引用已经变化，请确认游戏版本。")
    data["skeletonDataAsset"] = ptr(index, SKELETON)
    renderer.save_typetree(data)

    for ident in material_ids:
        material = cab.objects[ident]
        data = material.read_typetree()
        entry = material_texture(data)
        link = entry["m_Texture"]
        if link["m_FileID"] == 0:
            old = cab.objects.get(link["m_PathID"])
            if old is None or old.type.name != "Texture2D" or old.peek_name() not in ("P14409", "P14409_2"):
                raise ResourceError(f"召唤模型 {key} 的贴图引用已经变化。")
        elif not resolves(cab, link, external.name, TEXTURE):
            raise ResourceError(f"召唤模型 {key} 已指向其他贴图。")
        entry["m_Texture"] = ptr(index, TEXTURE)
        material.save_typetree(data)

    descriptor, = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    data = descriptor.read()
    dependency = relative(SHARED).removeprefix("0000/")
    if dependency not in data.m_Dependencies:
        data.m_Dependencies.append(dependency)
        data.save()
    result = pack(bundle, {})
    checked, _, cab = load(result)
    assert resolves(cab, cab.objects[renderer_id].read_typetree()["skeletonDataAsset"], external.name, SKELETON)
    assert all(resolves(cab, material_texture(cab.objects[ident].read_typetree())["m_Texture"], external.name, TEXTURE)
               for ident in material_ids)
    assert dependency in next(obj for obj in checked.objects if obj.type.name == "AssetBundle").read().m_Dependencies
    verify_untouched(original, result, {renderer_id, descriptor.path_id, *material_ids})
    return result


def combine_pages(staging, original_atlas):
    text = (staging / "P14409.atlas").read_text(encoding="utf-8-sig")
    pages = [part.splitlines() for part in re.split(r"\n[ \t]*\n", text.strip())]
    if not 1 <= len(pages) <= 2:
        raise ResourceError("14409 的召唤图集需要一到两张贴图页。")
    images = []
    for page in pages:
        with Image.open(staging / page[0]) as image:
            images.append(image.convert("RGBA"))
    gap = 4
    horizontal = sum(image.width for image in images) + gap * (len(images) - 1), max(image.height for image in images)
    vertical = max(image.width for image in images), sum(image.height for image in images) + gap * (len(images) - 1)
    choices = [(tuple((value + 3) // 4 * 4 for value in size), across)
               for size, across in ((horizontal, True), (vertical, False)) if max(size) <= 8192]
    if not choices:
        raise ResourceError("合并召唤贴图后超过 8192 像素，请缩小贴图。")
    size, across = min(choices, key=lambda choice: choice[0][0] * choice[0][1])
    canvas = Image.new("RGBA", size)
    headers = [line for line in original_atlas.splitlines() if line.startswith(("scale:", "filter:", "repeat:"))]
    lines = ["P14409.png", f"size:{size[0]},{size[1]}", "pma:true", *headers]
    x = y = 0
    for page, image in zip(pages, images):
        canvas.paste(image, (x, y))
        first_region = next(i for i, line in enumerate(page[1:], 1) if ":" not in line)
        for line in page[first_region:]:
            field, separator, value = line.strip().partition(":")
            if field in ("bounds", "xy") and separator:
                coordinates = [int(n) for n in value.split(",")]
                coordinates[0] += x
                coordinates[1] += y
                line = f"  {field}:{','.join(map(str, coordinates))}"
            lines.append(line)
        if across:
            x += image.width + gap
        else:
            y += image.height + gap
    return "\n".join(lines) + "\n", canvas


def edit_shared(original, staging):
    env, bundle, cab = load(original)
    skeleton = named(env, "P14409JS")
    atlas = named(env, "P14409.atlas")
    data = cab.objects[SKELETON].read_typetree()
    animation = cab.objects[ANIMATION].read_typetree()
    assert data["skeletonJSON"] == ptr(0, skeleton.path_id)
    assert animation["skeletonDataAsset"] == ptr(0, SKELETON)
    atlas_link, = data["atlasAssets"]
    atlas_data = cab.objects[atlas_link["m_PathID"]].read_typetree()
    assert atlas_link["m_FileID"] == 0 and atlas_data["atlasFile"] == ptr(0, atlas.path_id)
    material_link, = atlas_data["materials"]
    assert material_link["m_FileID"] == 0
    material = cab.objects[material_link["m_PathID"]].read_typetree()
    assert material_texture(material)["m_Texture"] == ptr(0, TEXTURE)
    skeleton_text = (staging / "P14409.json").read_text(encoding="utf-8-sig")
    if animation["animationName"] not in json.loads(skeleton_text).get("animations", {}):
        raise ResourceError(f"骨架 JSON 缺少召唤时间轴使用的动作「{animation['animationName']}」。")
    skeleton_asset = skeleton.read()
    edited = set()
    if skeleton_asset.m_Script != skeleton_text:
        skeleton_asset.m_Script = skeleton_text
        skeleton_asset.save()
        edited.add(skeleton.path_id)
    overrides = {}
    if (staging / "P14409.atlas").is_file():
        atlas_asset = atlas.read()
        atlas_text, canvas = combine_pages(staging, atlas_asset.m_Script)
        if atlas_signature(atlas_text) != atlas_signature(atlas_asset.m_Script):
            atlas_asset.m_Script = atlas_text
            atlas_asset.save()
            edited.add(atlas.path_id)
        else:
            atlas_text = atlas_asset.m_Script
        texture_obj = cab.objects[TEXTURE]
        texture = texture_obj.read()
        assert texture.m_Name == "P14409" and texture.m_TextureFormat == 25 and texture.m_MipCount == 1
        old_encoded = texture.get_image_data()
        old_size = texture.m_Width, texture.m_Height
        with canvas:
            encoded, fmt = image_to_texture2d(canvas, texture.m_TextureFormat)
            size = canvas.size
            if size == old_size:
                with texture.image.convert("RGBA") as old_image:
                    encoded = preserve_unchanged_blocks(old_image, canvas, old_encoded, encoded)
        assert fmt.value == texture.m_TextureFormat and len(encoded) == size[0] * size[1]
        if size != old_size or encoded != old_encoded:
            stream = texture.m_StreamData
            resource = ntpath.basename(stream.path)
            old = raw_entry(bundle.files[resource])
            offset, length = stream.offset, stream.size
            assert 0 <= offset <= offset + length <= len(old)
            if len(encoded) == length:
                overrides[resource] = old[:offset] + encoded + old[offset + length:]
            elif offset + length == len(old):
                overrides[resource] = old[:offset] + encoded
            else:
                stream.offset = len(old)
                overrides[resource] = old + encoded
            stream.size = len(encoded)
            texture.m_Width, texture.m_Height = size
            texture.m_CompleteImageSize = len(encoded)
            texture.save()
            edited.add(texture_obj.path_id)
    if not edited and not overrides:
        return original
    result = pack(bundle, overrides)
    checked, _, checked_cab = load(result)
    assert named(checked, "P14409JS").read().m_Script == skeleton_text
    if (staging / "P14409.atlas").is_file():
        assert named(checked, "P14409.atlas").read().m_Script == atlas_text
    if overrides:
        texture = checked_cab.objects[TEXTURE].read()
        assert (texture.m_Width, texture.m_Height, texture.m_TextureFormat, texture.m_MipCount) == (*size, 25, 1)
        assert texture.get_image_data() == encoded
    verify_untouched(original, result, edited)
    return result


def atlas_signature(text):
    return tuple((name.strip(), re.sub(r"\s+", "", value)) if sep else (name.strip(), "")
                 for line in text.splitlines() if line.strip()
                 for name, sep, value in (line.partition(":"),))


def preserve_unchanged_blocks(before, after, original, encoded):
    width, height = before.size
    assert width % 4 == 0 and height % 4 == 0
    assert len(original) == len(encoded) == width * height
    r, g, b, a = ImageChops.difference(before, after).split()
    visible = ImageChops.lighter(ImageChops.lighter(r, g), ImageChops.lighter(b, a))
    mask = visible.point([0] + [255] * 255).resize((width // 4, height // 4), Image.Resampling.BOX)
    blocks = mask.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes()
    output = bytearray(original)
    for block, changed in enumerate(blocks):
        if changed:
            start = block * 16
            output[start:start + 16] = encoded[start:start + 16]
    return bytes(output)


def extract_shared(game, model, target, force):
    if model not in MODELS:
        raise ResourceError("无法识别所选的 14409 召唤版本。")
    if not force:
        _, _, renderer = load((game / relative(model)).read_bytes())
        link = renderer.objects[MODELS[model][0]].read_typetree()["skeletonDataAsset"]
        if uses_local_skeleton(renderer, link):
            return False
    path = game / relative(SHARED)
    if not path.is_file():
        raise ResourceError("缺少模型引用的共享召唤资源，请先在游戏中载入这张卡。")
    raw = path.read_bytes()
    env, _, shared = load(raw)
    if not force and not resolves(renderer, link, shared.name, SKELETON):
        raise ResourceError("召唤模型的骨架引用已经变化，无法确认导出来源。")
    skeleton = named(env, "P14409JS")
    atlas = named(env, "P14409.atlas")
    data = shared.objects[SKELETON].read_typetree()
    assert data["skeletonJSON"] == ptr(0, skeleton.path_id)
    atlas_link, = data["atlasAssets"]
    assert atlas_link["m_FileID"] == 0
    assert shared.objects[atlas_link["m_PathID"]].read_typetree()["atlasFile"] == ptr(0, atlas.path_id)
    texture = shared.objects[TEXTURE].read()
    assert texture.m_Name == "P14409"
    (target / "P14409.json").write_bytes(skeleton.read().m_Script.encode("utf-8"))
    (target / "P14409.atlas").write_bytes(atlas.read().m_Script.encode("utf-8"))
    with texture.image as image:
        image.save(target / "P14409.png")
    if path.read_bytes() != raw:
        raise ResourceError("导出期间共享召唤资源发生变化，请重试。")
    return True


def main(game, expected_hash, staging, output):
    required = {key: game / relative(key) for key in (*MODELS, SHARED, TIMELINE)}
    missing = [key for key, path in required.items() if not path.is_file()]
    if missing:
        raise ResourceError("游戏尚缺召唤资源：" + "、".join(missing) + "。请在游戏里载入该卡的召唤动画。")
    original = {key: path.read_bytes() for key, path in required.items()}
    if sha256(original[SHARED]).hexdigest().lower() != expected_hash.lower():
        raise ResourceError("读取后共享资源发生变化，请重新打包。")
    _, _, timeline = load(original[TIMELINE])
    _, _, shared = load(original[SHARED])
    clip = timeline.objects[5025005778889516634].read_typetree()["template"]
    if not resolves(timeline, clip["animationReference"], shared.name, ANIMATION):
        raise ResourceError("游戏的召唤时间轴已更新，无法确认其共享资源。")
    external = timeline.externals[clip["animationReference"]["m_FileID"] - 1]
    edited_shared = edit_shared(original[SHARED], staging)
    if edited_shared == original[SHARED]:
        return False
    replacements = {SHARED: edited_shared}
    replacements.update({key: edit_model(key, original[key], external) for key in MODELS})
    if any(path.read_bytes() != original[key] for key, path in required.items()):
        raise ResourceError("打包期间游戏资源发生变化，请重试。")
    with ZipFile(output, "x", ZIP_STORED) as archive:
        for key, replacement in replacements.items():
            archive.writestr("替换/" + relative(key), replacement)
            archive.writestr("原始/" + relative(key), original[key])
    with ZipFile(output) as archive:
        assert archive.testzip() is None
    return True


if __name__ == "__main__":
    try:
        if sys.argv[1] == "--extract":
            game, model, staging, source = sys.argv[2:]
            if not extract_shared(Path(game), model, Path(staging), source == "shared"):
                sys.exit(3)
        else:
            game, digest, staging, output = sys.argv[1:]
            if not main(Path(game), digest, Path(staging), Path(output)):
                sys.exit(3)
    except ResourceError as error:
        print(error)
        sys.exit(2)
