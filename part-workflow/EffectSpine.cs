using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using CardAssetWorkbench;

namespace PartWorkflow;

internal static class EffectSpine
{
    private sealed record Input(EffectNativeModel Model, byte[] Skeleton, byte[] Atlas, string Directory);

    internal static object ReadSource(EffectFolderRequest request)
    {
        var (folder, map) = EffectPackage.ReadFolder(request.Folder, request.CardId);
        var input = ReadInput(folder, map);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(input.Skeleton).TrimStart('\ufeff'));
        return new
        {
            Skeleton = json.RootElement.Clone(), Atlas = Encoding.UTF8.GetString(input.Atlas),
            SourceSha256 = ProjectStore.HashBytes(input.Skeleton), AtlasSha256 = ProjectStore.HashBytes(input.Atlas)
        };
    }

    private static Input ReadInput(string folder, EffectMap map)
    {
        var models = map.NativeModels ?? [];
        if (models.Length == 0)
        {
            if (Directory.EnumerateFiles(folder, "原Spine-*.json").Any())
                throw new InvalidDataException("此效果素材包缺少原生 Spine 回包映射，请用新版重新提取，再复制编辑后的导出文件。");
            throw new InvalidDataException("此效果使用 Unity 动作。可回包贴图，或创建部件替换项目。");
        }
        if (models.Any(m => Path.GetFileName(m.Model) != m.Model || !m.Model.StartsWith("原Spine-", StringComparison.Ordinal) ||
            !m.Model.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("原生 Spine 模型文件名无效。");
        var edited = Path.Combine(folder, "Rigby2D导出");
        var directory = Directory.Exists(edited) ? edited : folder;
        bool Complete(EffectNativeModel m) => File.Exists(ProjectStore.Under(directory, m.Model)) &&
            File.Exists(ProjectStore.Under(directory, Path.ChangeExtension(m.Model, ".atlas")));
        var candidates = models.Where(Complete).ToArray();
        if (directory == folder && candidates.Length > 1)
        {
            candidates = candidates.Where(m =>
                !ProjectStore.Hash(ProjectStore.Under(folder, m.Model)).Equals(m.JsonSha256, StringComparison.OrdinalIgnoreCase) ||
                !ProjectStore.Hash(ProjectStore.Under(folder, Path.ChangeExtension(m.Model, ".atlas"))).Equals(m.AtlasSha256, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        if (candidates.Length != 1)
            throw new InvalidDataException("请在「Rigby2D导出」子文件夹中放一份改好的原Spine JSON、同名 atlas 和 PNG。");
        var model = candidates[0];
        return new(model, File.ReadAllBytes(ProjectStore.Under(directory, model.Model)),
            File.ReadAllBytes(ProjectStore.Under(directory, Path.ChangeExtension(model.Model, ".atlas"))), directory);
    }

    internal static (List<(EffectTarget Target, byte[] Data)> Edits, string[] Dependencies) CreateEdits(
        string workspace, string work, string folder, GameSettings settings, EffectMap map, SummonConversion conversion,
        Dictionary<string, string> prepared)
    {
        var input = ReadInput(folder, map);
        var bytes = SummonPackage.ApplyConversion(input.Skeleton, input.Atlas, conversion)!;
        var edited = Atlas.Parse(Encoding.UTF8.GetString(input.Atlas));
        var sourceAtlas = Atlas.Parse(File.ReadAllText(ProjectStore.Under(folder, Path.ChangeExtension(input.Model.Model, ".atlas"))));
        CheckRegionFrames(sourceAtlas, edited);
        var catalog = EffectCatalog.Read(workspace).SingleOrDefault(card => card.CardId == map.CardId);
        if (catalog is null || string.IsNullOrWhiteSpace(input.Model.Group) || !catalog.Prefabs.Concat(catalog.Timelines).Any(
            entry => entry.LogicalPath.StartsWith(input.Model.Group + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("无法确认原生 Spine 效果所属的游戏目录。请更新目录并重新提取。");
        var dependencies = SummonDependencies.ReadEffect(workspace, settings, map.CardId, input.Model.Group, work);
        if (dependencies.Models.Length == 0)
            throw new InvalidDataException("未找到游戏实际使用的原生 Spine 效果骨架。");
        var edits = new Dictionary<(string Bundle, int File, long Id), (EffectTarget Target, byte[] Data)>();
        foreach (var reference in dependencies.Models)
        {
            var locator = reference.Skeleton;
            var model = map.NativeModels!.SingleOrDefault(m => m.Group == input.Model.Group &&
                Path.GetFileName(m.Skeleton.Bundle) == locator.BundleId && m.Skeleton.PathId == locator.PathId &&
                m.Skeleton.FileIndex == locator.FileIndex)
                ?? throw new InvalidDataException("游戏实际使用的骨架与素材映射不符，请重新提取。");
            var original = ReadText(settings, map.CardId, model.Skeleton);
            var originalAtlas = Atlas.Parse(File.ReadAllText(ProjectStore.Under(work, reference.AtlasFile)));
            var (data, skeleton, _) = SummonSkeleton.Prepare(bytes, original);
            foreach (var animation in JsonNode.Parse(original)!["animations"]!.AsObject())
                if (skeleton["animations"]?[animation.Key] is null)
                    throw new InvalidDataException($"转换素材缺少游戏动作「{animation.Key}」。");
            SummonSkeleton.CheckRegions(skeleton, originalAtlas);
            foreach (var clip in reference.Animations)
                if (skeleton["animations"]?[clip] is null)
                    throw new InvalidDataException($"转换素材缺少时间轴调用的动作「{clip}」。");
            var targetAtlas = model.Model == input.Model.Model ? sourceAtlas :
                Atlas.Parse(File.ReadAllText(ProjectStore.Under(folder, Path.ChangeExtension(model.Model, ".atlas"))));
            CheckRegionFrames(targetAtlas, originalAtlas);
            if (MatchAtlasUvs(skeleton, edited, originalAtlas))
                data = Encoding.UTF8.GetBytes(skeleton.ToJsonString());
            if (model.Model != input.Model.Model && input.Directory == folder &&
                sourceAtlas.Pages.Keys.Any(name => map.Pages.Any(page => page.File == name &&
                    !ProjectStore.Hash(ProjectStore.Under(folder, name)).Equals(page.OriginalPngSha256, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("两种画质共用时间轴图集时，请恢复根目录的原贴图，将改好的 JSON、atlas 和 PNG 放到「Rigby2D导出」。");
            if (model.Model != input.Model.Model && input.Directory != folder && prepared.Count > 0)
            {
                var sourceName = Path.ChangeExtension(input.Model.Model, ".atlas");
                var destinationName = Path.ChangeExtension(model.Model, ".atlas");
                var sizes = map.Pages.ToDictionary(p => p.File, p => (p.Width, p.Height), StringComparer.OrdinalIgnoreCase);
                var sync = Path.Combine(work, "synced-spine-pages");
                Directory.CreateDirectory(sync);
                foreach (var (page, path) in EffectAtlasEdits.Transfer(folder, input.Directory,
                    ProjectStore.Under(folder, destinationName), ProjectStore.Under(input.Directory, sourceName), sync,
                    sizes, resize: true, compareOriginal: ProjectStore.Under(folder, sourceName)))
                    prepared[page] = path;
            }
            var target = model.Skeleton;
            edits[(target.Bundle, target.FileIndex, target.PathId)] = (target, data);
        }
        if (edits.Count == 0) throw new InvalidDataException("找不到可写回的原生 Spine 骨架。");
        return (edits.Values.ToList(), dependencies.BundleIds);
    }

    private static bool MatchAtlasUvs(JsonObject skeleton, Atlas edited, Atlas target)
    {
        var changed = false;
        foreach (var skin in skeleton["skins"]!.AsArray())
            foreach (var slot in skin!["attachments"]!.AsObject())
                foreach (var attachment in slot.Value!.AsObject())
                {
                    var data = attachment.Value!;
                    if (data["uvs"] is not JsonArray uvs) continue;
                    var name = data["path"]?.GetValue<string>() ?? attachment.Key;
                    var from = edited.MakePart(0, name, name, new JsonObject());
                    var to = target.MakePart(0, name, name, new JsonObject());
                    if (from.Width == to.Width && from.Height == to.Height && from.Offsets.SequenceEqual(to.Offsets) &&
                        from.Bounds.Skip(2).SequenceEqual(to.Bounds.Skip(2))) continue;
                    var fromTop = from.Height - from.Bounds[3] - from.Offsets[1];
                    var toTop = to.Height - to.Bounds[3] - to.Offsets[1];
                    for (var i = 0; i < uvs.Count; i += 2)
                    {
                        var u = (uvs[i]!.GetValue<double>() * from.Width - from.Offsets[0]) / from.Bounds[2];
                        var v = (uvs[i + 1]!.GetValue<double>() * from.Height - fromTop) / from.Bounds[3];
                        uvs[i] = (u * to.Bounds[2] + to.Offsets[0]) / to.Width;
                        uvs[i + 1] = (v * to.Bounds[3] + toTop) / to.Height;
                    }
                    changed = true;
                }
        return changed;
    }

    private static void CheckRegionFrames(Atlas source, Atlas other)
    {
        foreach (var region in source.Regions.Keys)
        {
            if (!other.Regions.ContainsKey(region))
                throw new InvalidDataException($"另一份图集缺少部件「{region}」。");
            var a = source.MakePart(0, region, region, new JsonObject());
            var b = other.MakePart(0, region, region, new JsonObject());
            // The generated mesh UVs are normalized to each original image canvas.
            var left = Margins(a);
            var right = Margins(b);
            if (left.Zip(right).Any(pair => Math.Abs(pair.First - pair.Second) > 2.0 / Math.Min(a.Width, b.Width) +
                2.0 / Math.Min(a.Height, b.Height)))
                throw new InvalidDataException($"部件「{region}」的图集裁切范围不同，无法共用转换后的网格。");
        }
        static double[] Margins(Part p) =>
            [(double)p.Offsets[0] / p.Width, (double)p.Offsets[1] / p.Height,
             (double)(p.Width - p.Bounds[2] - p.Offsets[0]) / p.Width,
             (double)(p.Height - p.Bounds[3] - p.Offsets[1]) / p.Height];
    }

    private static byte[] ReadText(GameSettings settings, int cardId, EffectTarget target)
    {
        var match = EffectPackage.BundlePath.Match(target.Bundle);
        if (!match.Success || !match.Groups[2].Value.StartsWith(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ||
            target.FileIndex is < 0 or > 64 || string.IsNullOrWhiteSpace(target.Name))
            throw new InvalidDataException("原生 Spine 游戏资源定位无效。");
        var path = ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}/{target.Bundle}");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少游戏效果资源：" + target.Bundle);
        var manager = AssetIndex.Manager();
        try
        {
            var bundle = manager.LoadBundleFile(path, true);
            if (target.FileIndex >= bundle.file.BlockAndDirInfo.DirectoryInfos.Count || !bundle.file.IsAssetsFile(target.FileIndex))
                throw new InvalidDataException("原生 Spine Bundle 缺少对应的资源表。");
            var asset = manager.LoadAssetsFileFromBundle(bundle, target.FileIndex, false);
            manager.LoadClassDatabaseFromPackage(asset.file.Metadata.UnityVersion);
            if (!asset.file.GetAssetsOfType(AssetClassID.AssetBundle).Any(info =>
                EffectPackage.HasEffectContainer(manager.GetBaseField(asset, info), cardId)))
                throw new InvalidDataException("原生 Spine Bundle 与所选卡片不符。");
            var item = asset.file.GetAssetInfo(target.PathId);
            if (item?.TypeId != (int)AssetClassID.TextAsset) throw new InvalidDataException("找不到原生 Spine 文本资源。");
            var field = manager.GetBaseField(asset, item);
            if (field["m_Name"].AsString != target.Name) throw new InvalidDataException("原生 Spine 文本资源名称与映射不符。");
            try { return field["m_Script"].AsByteArray; }
            catch { return field["m_Script"]["Array"].AsByteArray; }
        }
        finally { manager.UnloadAll(); }
    }
}
