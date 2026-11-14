using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

internal sealed record SummonPackRequest(string Folder, int CardId, string Variant, SummonConversion? Conversion = null);
internal sealed record SummonConversion(string SourceSha256, string AtlasSha256, JsonElement Skeleton);
internal sealed record SummonSourceMap(string Format, int CardId, string Variant, string AtlasSha256,
    string SkeletonSha256, Dictionary<string, string> Pages, string? SourceBundle = null);

internal static class SummonPackage
{
    internal const string MapName = "召唤贴图映射.json";
    internal const string MapFormat = "md-summon-textures-v1";

    private static string ReadFolder(SummonPackRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Folder))
            throw new InvalidDataException("请选择已解压的召唤素材文件夹。");
        if (request.CardId <= 0 || string.IsNullOrWhiteSpace(request.Variant))
            throw new InvalidDataException("请选择导出时使用的召唤卡片和画质。");
        var folder = Path.GetFullPath(request.Folder.Trim().Trim('"'));
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("请选择存在的召唤素材文件夹。");
        return folder;
    }

    private static SummonSourceMap? ReadMap(string folder, int cardId)
    {
        if (File.Exists(Path.Combine(folder, EffectPackage.MapName)))
            throw new InvalidDataException("这是卡片效果素材，请使用「打包效果替换文件」。");
        var mapPath = Path.Combine(folder, MapName);
        var map = File.Exists(mapPath) ? JsonFile.Read<SummonSourceMap>(mapPath) : null;
        if (map is not null && (map.Format != MapFormat || map.CardId != cardId))
            throw new InvalidDataException("素材包的卡片与首页选择不一致。");
        return map;
    }

    private static string? FindInputSkeleton(string folder, string atlas)
    {
        var name = Path.GetFileNameWithoutExtension(atlas) + ".json";
        var nested = Path.Combine(folder, "Rigby2D导出", name);
        var root = Path.Combine(folder, name);
        return File.Exists(nested) ? nested : File.Exists(root) ? root : null;
    }

    private static string FindInputAtlas(string folder, string atlas)
    {
        var name = Path.GetFileName(atlas);
        var nested = Path.Combine(folder, "Rigby2D导出", name);
        return File.Exists(nested) ? nested : Path.Combine(folder, name);
    }

    internal static object ReadConversionSource(ProjectStore store, SummonPackRequest request)
    {
        var folder = ReadFolder(request);
        var map = ReadMap(folder, request.CardId);
        var input = ReadConversionInput(store, request, folder, map);
        var skeleton = File.ReadAllBytes(input.Skeleton);
        var layout = File.ReadAllBytes(input.Atlas);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(skeleton).TrimStart('\ufeff'));
        return new
        {
            Skeleton = document.RootElement.Clone(), Atlas = Encoding.UTF8.GetString(layout),
            SourceSha256 = ProjectStore.HashBytes(skeleton), AtlasSha256 = ProjectStore.HashBytes(layout)
        };
    }

    private static (string Folder, SummonSourceMap? Map, string Skeleton, string Atlas) ReadConversionInput(
        ProjectStore store, SummonPackRequest request, string folder, SummonSourceMap? map)
    {
        var (_, _, _, _, _, _, atlas) = store.OpenModel(new(request.CardId, map?.Variant ?? request.Variant));
        bool HasPair(string path)
        {
            var (skeleton, layout) = InputFiles(path, atlas, exactFolder: true);
            return skeleton is not null && File.Exists(layout);
        }

        var named = Path.Combine(folder, "Rigby2D导出");
        var candidates = new[] { named, folder }.Where(HasPair).Take(1).ToArray();
        if (candidates.Length == 0)
            candidates = Directory.EnumerateDirectories(folder).Where(HasPair).Take(2).ToArray();
        if (candidates.Length == 0) throw MissingConversionFiles(atlas);
        if (candidates.Length > 1)
            throw new InvalidDataException("找到多个含有该卡导出文件的子文件夹。请选择要打包的那一个。");
        var selected = candidates[0];
        var selectedMap = selected == folder ? map : ReadMap(selected, request.CardId) ?? map;
        if (selectedMap?.Variant is { } variant && variant != (map?.Variant ?? request.Variant))
            (_, _, _, _, _, _, atlas) = store.OpenModel(new(request.CardId, variant));
        var (skeleton, layout) = InputFiles(selected, atlas, exactFolder: true);
        if (skeleton is null || !File.Exists(layout)) throw MissingConversionFiles(atlas);
        return (selected, selectedMap, skeleton, layout);
    }

    private static InvalidDataException MissingConversionFiles(string atlas) =>
        new($"找不到成对的「{Path.GetFileNameWithoutExtension(atlas)}.json」和「{Path.GetFileName(atlas)}」。请选择同时包含这两个文件的 Rigby2D 导出文件夹。");

    internal static (string? Skeleton, string Atlas) InputFiles(string folder, string atlas, bool exactFolder)
    {
        if (!exactFolder) return (FindInputSkeleton(folder, atlas), FindInputAtlas(folder, atlas));
        var name = Path.GetFileName(atlas);
        var skeleton = Path.Combine(folder, Path.ChangeExtension(name, ".json"));
        return (File.Exists(skeleton) ? skeleton : null, Path.Combine(folder, name));
    }

    internal static byte[]? ApplyConversion(byte[]? skeleton, byte[]? atlas, SummonConversion? conversion)
    {
        if (conversion is null) return skeleton;
        if (skeleton is null || atlas is null ||
            !ProjectStore.HashBytes(skeleton).Equals(conversion.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !ProjectStore.HashBytes(atlas).Equals(conversion.AtlasSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("转换期间素材 JSON 或 atlas 已变动，请重新打包。");
        if (conversion.Skeleton.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("未收到有效的转换骨架，请重新打包。");
        return Encoding.UTF8.GetBytes(conversion.Skeleton.GetRawText());
    }

    internal static (FileStream Content, string FileName, List<string> Repairs) Pack(
        ProjectStore store, GameSettings settings, SummonPackRequest request)
    {
        if (string.IsNullOrWhiteSpace(settings.GameRoot) || string.IsNullOrWhiteSpace(settings.Account))
            throw new InvalidOperationException("请先在首页应用游戏目录和账号。");
        var folder = ReadFolder(request);
        var map = ReadMap(folder, request.CardId);
        if (request.Conversion is not null)
            (folder, map, _, _) = ReadConversionInput(store, request, folder, map);

        var (card, variant, version, _, source, skeleton, atlas) = store.OpenModel(new(request.CardId, map?.Variant ?? request.Variant));
        var work = Path.Combine(AppEnvironment.Runtime(store.Workspace), $"summon-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var dependencies = SummonDependencies.Read(store.Workspace, settings, card.Id, variant, work);
            if (SummonSharedPackage.Applies(card.Id, variant) && !dependencies.Models.Any(model =>
                model.Qualities.Contains(SummonDependencies.Quality(variant)) && model.Skeleton.BundleId == SummonSharedPackage.BundleId))
                return SummonSharedPackage.Pack(store.Workspace, settings, card, variant, source, skeleton, atlas, folder, map, request.Conversion);
            using var active = SummonSharedPackage.Applies(card.Id, variant)
                ? SummonSharedPackage.OpenExportSource(store.Workspace, settings, variant) : null;
            if (active is not null && (map?.SourceBundle == SummonSharedPackage.BundleId || map is null))
            {
                source = active.Folder;
                skeleton = "P14409.json";
                atlas = "P14409.atlas";
            }
            var label = SummonDependencies.Region(variant).Replace('/', '-');
            var name = BulkLibrary.SafeComponent($"{card.Id}-{card.Name}-{label}-召唤替换") + ".zip";
            var (originals, edits, repairs) = Prepare(store, settings, card, variant, version, source, skeleton, atlas, folder, map, work,
                dependencies, synchronizeInput: true, conversion: request.Conversion);
            return (PackBundles(work, settings, originals, originals.Keys, edits), name, repairs);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    internal static (Dictionary<string, string> Originals, List<GameReplacementExporter.Edit> Edits, List<string> Repairs) Prepare(
        ProjectStore store, GameSettings settings, CardChoice card, string variant, string version, string source,
        string skeleton, string atlas, string folder, SummonSourceMap? map, string work,
        SummonDependencies? resolved = null, bool synchronizeInput = false, SummonConversion? conversion = null)
    {
        var originalSkeleton = File.ReadAllBytes(ProjectStore.Under(source, skeleton));
        var originalAtlas = File.ReadAllBytes(ProjectStore.Under(source, atlas));
        var gameAtlas = Atlas.Parse(Encoding.UTF8.GetString(originalAtlas));
        var pageHashes = gameAtlas.Pages.Keys.ToDictionary(name => name, name => ProjectStore.Hash(ProjectStore.Under(source, name)));
        var dimensions = gameAtlas.Pages.Keys.ToDictionary(name => name, name => RigbyExport.TextureSize(ProjectStore.Under(source, name)));
        var normalizedAtlas = RigbyExport.NormalizeAtlas(originalAtlas, dimensions);
        var (editedSkeletonPath, editedAtlasPath) = InputFiles(folder, atlas, exactFolder: conversion is not null);
        var imageFolder = Path.GetDirectoryName(editedAtlasPath)!;
        var suppliedAtlas = File.Exists(editedAtlasPath) ? File.ReadAllBytes(editedAtlasPath) : null;
        var editedSkeleton = ApplyConversion(editedSkeletonPath is null ? null : File.ReadAllBytes(editedSkeletonPath),
            suppliedAtlas, conversion) ?? originalSkeleton;
        var editedAtlas = suppliedAtlas ?? normalizedAtlas;
        static bool Matches(byte[] data, string? hash) => hash is not null &&
            ProjectStore.HashBytes(data).Equals(hash, StringComparison.OrdinalIgnoreCase);
        var sameLayout = editedAtlas.AsSpan().SequenceEqual(originalAtlas) || editedAtlas.AsSpan().SequenceEqual(normalizedAtlas)
            || Matches(editedAtlas, map?.AtlasSha256);
        var inputAtlas = Atlas.Parse(Encoding.UTF8.GetString(editedAtlas));
        if (inputAtlas.Pages.Count == 0) throw new InvalidDataException("atlas 里没有贴图页。");
        if (editedSkeletonPath is null && !File.Exists(editedAtlasPath) &&
            !inputAtlas.Pages.Keys.Any(name => File.Exists(ProjectStore.Under(imageFolder, name))))
            throw new InvalidDataException("找不到所选动画的骨架 JSON、atlas 或 PNG。");
        var skeletonChanged = !editedSkeleton.AsSpan().SequenceEqual(originalSkeleton) && !Matches(editedSkeleton, map?.SkeletonSha256);
        var images = new Dictionary<string, string>();
        var changedPages = new HashSet<string>();
        var suppliedPages = new HashSet<string>();
        var textureChanged = !sameLayout;
        foreach (var name in inputAtlas.Pages.Keys)
        {
            var image = ProjectStore.Under(imageFolder, name);
            if (File.Exists(image))
            {
                var baselineHash = map?.Pages?.GetValueOrDefault(name) ?? pageHashes.GetValueOrDefault(name);
                if (!ProjectStore.Hash(image).Equals(baselineHash, StringComparison.OrdinalIgnoreCase)) changedPages.Add(name);
                images.Add(name, image);
                suppliedPages.Add(name);
            }
            else if (sameLayout && pageHashes.TryGetValue(name, out var hash) &&
                (map?.Pages?.GetValueOrDefault(name) is not { } expected || hash.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                images.Add(name, ProjectStore.Under(source, name));
        }
        textureChanged |= changedPages.Count > 0;
        if (textureChanged && images.Count != inputAtlas.Pages.Count)
            throw new FileNotFoundException("本次图集同步缺少同次导出的贴图页：" +
                string.Join("、", inputAtlas.Pages.Keys.Except(images.Keys)));
        var imageSizes = images.ToDictionary(x => x.Key, x => RigbyExport.TextureSize(x.Value));
        if (imageSizes.Values.Any(size => size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192))
            throw new InvalidDataException("贴图的宽高需在 1 至 8192 像素之间。");
        var syncTextures = synchronizeInput && suppliedPages.Count > 0;
        if (syncTextures && images.Count != inputAtlas.Pages.Count)
            throw new FileNotFoundException("请提供同一份 Spine 素材的完整贴图页，以便同步所有画质：" +
                string.Join("、", inputAtlas.Pages.Keys.Except(images.Keys)));
        if (textureChanged || syncTextures)
        {
            inputAtlas = Atlas.Parse(Encoding.UTF8.GetString(RigbyExport.NormalizeAtlas(editedAtlas, imageSizes)));
            CheckAtlasBounds(inputAtlas, imageSizes);
        }

        var dependencies = resolved ?? SummonDependencies.Read(store.Workspace, settings, card.Id, variant, work);
        var selectedQuality = SummonDependencies.Quality(variant);
        var selectedAtlases = dependencies.Models.Where(model => model.Qualities.Contains(selectedQuality))
            .Select(model => model.Atlas).ToHashSet();
        var repairs = new List<string>();
        var syncSkeleton = synchronizeInput && editedSkeletonPath is not null;
        var sharedSkeleton = editedSkeleton;
        if (syncSkeleton)
        {
            var (prepared, _, fixes) = SummonSkeleton.Prepare(editedSkeleton, originalSkeleton);
            sharedSkeleton = prepared;
            repairs.AddRange(fixes);
        }
        if (dependencies.Models.Length > 1 && (syncSkeleton || skeletonChanged || textureChanged || syncTextures))
        {
            var relevant = syncSkeleton || skeletonChanged ? new[] { sharedSkeleton } :
                dependencies.Models.Where(model => model.Animations.Length > 0)
                    .Select(model => File.ReadAllBytes(Path.Combine(work, model.SkeletonFile)));
            if (relevant.Any(SummonSkeleton.HasMeshDeform))
            {
                var linked = SummonDependencies.Read(store.Workspace, settings, card.Id, variant, work, shareBindings: true);
                if (linked.Hashes.Any(entry => dependencies.Hashes.TryGetValue(entry.Key, out var prior) && prior != entry.Value))
                    throw new IOException("游戏召唤资源在分析期间发生变化，请重新打包。");
                dependencies = linked;
            }
        }
        var originals = dependencies.Hashes;
        var targets = dependencies.Models.ToList();
        if (targets.Count == 0)
        {
            foreach (var choice in new[] { variant }.Concat(SummonDependencies.Companions(card, variant)))
            {
                var other = choice == variant ? (Version: version, Source: source, SkeletonFile: skeleton, AtlasFile: atlas)
                    : OpenCompanion(store, card.Id, choice);
                targets.Add(CatalogTarget(other.Version, other.Source, other.SkeletonFile, other.AtlasFile, choice, originals));
            }
        }
        else
        {
            var qualities = new[] { variant }.Concat(SummonDependencies.Companions(card, variant)).Select(SummonDependencies.Quality).Distinct();
            foreach (var quality in qualities)
                if (!targets.Any(target => target.Qualities.Contains(quality)))
                    throw new InvalidDataException($"索引无法确认该卡的{(quality == "sd" ? "低清" : "高清")}模型引用。请先更新动画目录。");
        }

        var edits = new Dictionary<GameReplacementExporter.Locator, GameReplacementExporter.Edit>();
        foreach (var target in targets)
        {
            // A complete input can match the selected quality exactly while the other model
            // or the timeline still refers to a different version of its resources.
            var otherTarget = target.Qualities.Length == 0 || target.Qualities.Any(quality => quality != selectedQuality);
            var targetAtlas = File.ReadAllBytes(Path.Combine(work, target.AtlasFile));
            var selectedAtlas = selectedAtlases.Contains(target.Atlas) && targetAtlas.AsSpan().SequenceEqual(originalAtlas);
            var forceTexture = syncTextures && otherTarget && !(selectedAtlas && sameLayout && changedPages.Count == 0);
            var applyTexture = textureChanged || forceTexture;
            if (!skeletonChanged && !syncSkeleton && !applyTexture) continue;
            var targetSkeleton = File.ReadAllBytes(Path.Combine(work, target.SkeletonFile));
            var (bytes, model, fixes) = SummonSkeleton.Prepare(syncSkeleton ? sharedSkeleton :
                skeletonChanged ? editedSkeleton : targetSkeleton, targetSkeleton);
            if (syncSkeleton && !bytes.AsSpan().SequenceEqual(sharedSkeleton))
                throw new InvalidDataException("无法给两个画质使用同一份骨架。请在 Spine 素材中修正约束顺序后重试。");
            repairs.AddRange(fixes);
            foreach (var animation in target.Animations)
                if (model["animations"]?[animation] is null)
                    throw new InvalidDataException($"骨架 JSON 缺少游戏时间轴调用的动作「{animation}」。");
            var output = applyTexture
                ? SummonAtlasTransfer.Prepare(work, target, targetAtlas, inputAtlas, images, imageSizes,
                    sameLayout && !forceTexture ? changedPages : null, edits)
                : Atlas.Parse(Encoding.UTF8.GetString(targetAtlas));
            SummonSkeleton.CheckRegions(model, output);
            if (!bytes.AsSpan().SequenceEqual(targetSkeleton))
                AddEdit(edits, new(target.Skeleton, null, bytes));
        }
        return (originals, edits.Values.ToList(), repairs.Distinct().ToList());
    }

    private static (string Version, string Source, string SkeletonFile, string AtlasFile) OpenCompanion(ProjectStore store, int card, string variant)
    {
        var model = store.OpenModel(new(card, variant));
        return (model.Version, model.Source, model.SkeletonFile, model.AtlasFile);
    }

    private static SummonTarget CatalogTarget(string version, string source, string skeleton, string atlas, string variant,
        Dictionary<string, string> hashes)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(version, "manifest.json")))!;
        var assets = manifest["Assets"]!.Deserialize<LibraryAsset[]>()!;
        var descriptor = manifest["CatalogVariants"]?.Deserialize<CatalogVariant[]>()?.Single(v => v.Variant == variant);
        var skeletonPath = ProjectStore.Under(source, skeleton);
        var atlasPath = ProjectStore.Under(source, atlas);
        var pageNames = Atlas.Parse(File.ReadAllText(atlasPath)).Pages.Keys;
        var atlasAsset = descriptor?.Atlas ?? assets.Single(a => a.Asset.Type == "TextAsset" &&
            a.Sha256 == ProjectStore.Hash(atlasPath) && a.Asset.Name == atlas);
        var skeletonAsset = descriptor?.Skeleton ?? assets.Single(a => a.Asset.Type == "TextAsset" &&
            a.Sha256 == ProjectStore.Hash(skeletonPath) && a.Asset.Name == Path.GetFileNameWithoutExtension(skeleton));
        var pages = pageNames.Select(name => descriptor?.Pages.Single(a => a.Asset.Name == Path.GetFileNameWithoutExtension(name)) ??
            assets.Single(a => a.Asset.Type == "Texture2D" && a.Asset.Name == Path.GetFileNameWithoutExtension(name) &&
                Path.GetDirectoryName(a.LogicalPath) == Path.GetDirectoryName(atlasAsset.LogicalPath) &&
                a.Sha256 == ProjectStore.Hash(ProjectStore.Under(source, name)))).ToArray();
        var originals = manifest["OriginalFiles"]!.Deserialize<SourceDigest[]>()!.ToDictionary(x => x.BundleId, x => x.Sha256);
        foreach (var resource in pages.Prepend(atlasAsset).Prepend(skeletonAsset))
        {
            var hash = originals[resource.BundleId];
            if (hashes.TryGetValue(resource.BundleId, out var existing) && hash != existing)
                throw new InvalidDataException("两个画质的资源快照不同，请重新提取。");
            hashes[resource.BundleId] = hash;
        }
        return new(Locate(skeletonAsset), Locate(atlasAsset), pages.Select(Locate).ToArray(), skeletonPath, atlasPath,
            pageNames.Select(name => RigbyExport.TextureSize(ProjectStore.Under(source, name)))
                .Select(size => new[] { size.Width, size.Height }).ToArray(), [SummonDependencies.Quality(variant)], []);
    }

    internal static void AddEdit(Dictionary<GameReplacementExporter.Locator, GameReplacementExporter.Edit> edits,
        GameReplacementExporter.Edit edit)
    {
        if (edits.TryAdd(edit.Asset, edit)) return;
        var previous = edits[edit.Asset];
        var equal = edit.Text is { } text && previous.Text is { } existing
            ? text.AsSpan().SequenceEqual(existing)
            : edit.Png is { } png && previous.Png is { } path && ProjectStore.Hash(png) == ProjectStore.Hash(path);
        if (!equal) throw new InvalidDataException($"不同画质对共享资源「{edit.Asset.Name}」的修改冲突。");
    }

    private static GameReplacementExporter.Locator Locate(LibraryAsset asset) =>
        new(asset.BundleId, asset.Asset.FileIndex, asset.Asset.PathId, asset.Asset.Name, asset.Asset.Type);

    internal static void CheckAtlasBounds(Atlas atlas, Dictionary<string, (int Width, int Height)> sizes)
    {
        foreach (var region in atlas.Regions.Values)
        {
            var part = atlas.MakePart(0, region.Name, region.Name, new JsonObject());
            var bounds = part.Bounds;
            var size = sizes[region.Page];
            var rotated = part.Rotation % 180 != 0;
            if (bounds[0] < 0 || bounds[1] < 0 || bounds[2] <= 0 || bounds[3] <= 0 ||
                (long)bounds[0] + bounds[rotated ? 3 : 2] > size.Width ||
                (long)bounds[1] + bounds[rotated ? 2 : 3] > size.Height)
                throw new InvalidDataException($"atlas 中的部件「{region.Name}」超出贴图「{region.Page}」范围。");
        }
    }

    internal static byte[] WriteAtlas(Atlas atlas)
    {
        var text = new StringBuilder();
        foreach (var (name, page) in atlas.Pages)
        {
            text.AppendLine(name);
            foreach (var (field, value) in page) text.AppendLine($"{field}: {value}");
            foreach (var region in atlas.Regions.Values.Where(region => region.Page == name))
            {
                text.AppendLine(region.Name);
                foreach (var (field, value) in region.Fields) text.AppendLine($"  {field}: {value}");
            }
            text.AppendLine();
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static FileStream PackCurrentCopies(string work, GameSettings settings, string[] ids, string knownId, string knownHash)
    {
        var hashes = ids.ToDictionary(id => id, id =>
        {
            var live = GameBundlePath(settings, id);
            if (!File.Exists(live)) throw new FileNotFoundException("当前账号缺少召唤动画文件：" + id);
            return id == knownId ? knownHash : ProjectStore.Hash(live);
        });
        return PackBundles(work, settings, hashes, ids, []);
    }

    internal static string GameBundlePath(GameSettings settings, string id) =>
        ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}/0000/{id[..2]}/{id}");

    internal static FileStream PackBundles(string work, GameSettings settings, Dictionary<string, string> originals,
        IEnumerable<string> bundleIds, List<GameReplacementExporter.Edit> edits)
    {
        var stream = new FileStream(work + ".zip", FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, 65536, FileOptions.DeleteOnClose);
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var byBundle = edits.ToLookup(edit => edit.Asset.BundleId);
                foreach (var id in bundleIds.Concat(byBundle.Select(group => group.Key)).Distinct())
                {
                    if (id.Length != 8 || id.Any(c => !Uri.IsHexDigit(c)) || !originals.TryGetValue(id, out var hash))
                        throw new InvalidDataException("召唤资源的 Bundle 定位无效。");
                    var relative = $"0000/{id[..2]}/{id}";
                    var live = GameBundlePath(settings, id);
                    if (!File.Exists(live)) throw new FileNotFoundException("当前账号缺少召唤动画文件：" + relative);
                    var backup = Path.Combine(work, id + ".original");
                    File.Copy(live, backup);
                    if (ProjectStore.Hash(backup) != hash)
                        throw new InvalidDataException("游戏原文件已变化，请重新提取后再回包：" + relative);
                    var bindings = Path.Combine(work, id + ".bindings");
                    var replacement = File.Exists(bindings) ? bindings : backup;
                    if (byBundle.Contains(id))
                    {
                        var edited = Path.Combine(work, id + ".replacement");
                        File.Copy(replacement, edited);
                        replacement = edited;
                        GameReplacementExporter.Rewrite(replacement, byBundle[id].ToArray());
                    }
                    if (ProjectStore.Hash(live) != hash)
                        throw new IOException("打包期间游戏文件发生变化，请重试。");
                    zip.CreateEntryFromFile(backup, "原始/" + relative, CompressionLevel.NoCompression);
                    zip.CreateEntryFromFile(replacement, "替换/" + relative, CompressionLevel.NoCompression);
                }
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
