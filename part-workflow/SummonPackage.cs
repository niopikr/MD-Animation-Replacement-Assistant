using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

internal sealed record SummonPackRequest(string Folder, int CardId, string Variant);
internal sealed record SummonSourceMap(string Format, int CardId, string Variant, string AtlasSha256,
    string SkeletonSha256, Dictionary<string, string> Pages, string? SourceBundle = null);

internal static class SummonPackage
{
    internal const string MapName = "召唤贴图映射.json";
    internal const string MapFormat = "md-summon-textures-v1";

    internal static (FileStream Content, string FileName, List<string> Repairs) Pack(
        ProjectStore store, GameSettings settings, SummonPackRequest request)
    {
        if (string.IsNullOrWhiteSpace(settings.GameRoot) || string.IsNullOrWhiteSpace(settings.Account))
            throw new InvalidOperationException("请先在首页应用游戏目录和账号。");
        if (string.IsNullOrWhiteSpace(request.Folder))
            throw new InvalidDataException("请选择已解压的召唤素材文件夹。");
        if (request.CardId <= 0 || string.IsNullOrWhiteSpace(request.Variant))
            throw new InvalidDataException("请选择导出时使用的召唤卡片和画质。");
        var folder = Path.GetFullPath(request.Folder.Trim().Trim('"'));
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("请选择存在的召唤素材文件夹。");
        if (File.Exists(Path.Combine(folder, EffectPackage.MapName)))
            throw new InvalidDataException("这是卡片效果素材，请使用「打包效果替换文件」。");
        var mapPath = Path.Combine(folder, MapName);
        var map = File.Exists(mapPath) ? JsonFile.Read<SummonSourceMap>(mapPath) : null;
        if (map is not null && (map.Format != MapFormat || map.CardId != request.CardId || map.Variant != request.Variant))
            throw new InvalidDataException("素材包的卡片或画质与首页选择不一致，请选回导出时的卡片和画质。");

        var (card, variant, version, _, source, skeleton, atlas) = store.OpenModel(new(request.CardId, request.Variant));
        if (SummonSharedPackage.Applies(card.Id, variant))
            return SummonSharedPackage.Pack(store.Workspace, settings, card, variant, source, skeleton, atlas, folder, map);
        var stem = Path.GetFileNameWithoutExtension(atlas);
        var originalSkeleton = File.ReadAllBytes(ProjectStore.Under(source, skeleton));
        var originalAtlas = File.ReadAllBytes(ProjectStore.Under(source, atlas));
        var atlasHash = ProjectStore.HashBytes(originalAtlas);
        var gameAtlas = Atlas.Parse(Encoding.UTF8.GetString(originalAtlas));
        var pageHashes = gameAtlas.Pages.Keys.ToDictionary(name => name, name => ProjectStore.Hash(ProjectStore.Under(source, name)));
        var dimensions = gameAtlas.Pages.Keys.ToDictionary(name => name, name => RigbyExport.TextureSize(ProjectStore.Under(source, name)));
        var normalizedAtlas = RigbyExport.NormalizeAtlas(originalAtlas, dimensions);
        var rigbyFolder = Path.Combine(folder, "Rigby2D导出");
        var atlasFile = stem + ".atlas";
        var skeletonFile = stem + ".json";
        var editedSkeletonPath = new[] { Path.Combine(rigbyFolder, skeletonFile), Path.Combine(folder, skeletonFile) }
            .FirstOrDefault(File.Exists);
        var (editedSkeleton, model, repairs) = SummonSkeleton.Prepare(
            editedSkeletonPath is null ? originalSkeleton : File.ReadAllBytes(editedSkeletonPath), originalSkeleton);
        var imageFolder = File.Exists(Path.Combine(rigbyFolder, atlasFile)) ? rigbyFolder : folder;
        var editedAtlasPath = Path.Combine(imageFolder, atlasFile);
        var editedAtlas = File.Exists(editedAtlasPath) ? File.ReadAllBytes(editedAtlasPath) : normalizedAtlas;
        var sameLayout = editedAtlas.AsSpan().SequenceEqual(originalAtlas) || editedAtlas.AsSpan().SequenceEqual(normalizedAtlas);
        var inputAtlas = Atlas.Parse(Encoding.UTF8.GetString(editedAtlas));
        if (inputAtlas.Pages.Count == 0) throw new InvalidDataException("atlas 里没有贴图页。");
        var unknownPages = inputAtlas.Pages.Keys.Where(name => !gameAtlas.Pages.ContainsKey(name)).ToArray();
        if (unknownPages.Length > 0)
            throw new InvalidDataException($"所选游戏资源没有这些贴图页：{string.Join("、", unknownPages)}。请沿用原贴图页文件名。");
        if (editedSkeletonPath is null && !File.Exists(editedAtlasPath) &&
            !inputAtlas.Pages.Keys.Any(name => File.Exists(ProjectStore.Under(imageFolder, name))))
            throw new InvalidDataException("找不到所选动画的骨架 JSON、atlas 或 PNG。");

        var work = Path.Combine(AppEnvironment.Runtime(store.Workspace), $"summon-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(version, "manifest.json")))!;
            var assets = manifest["Assets"]!.Deserialize<LibraryAsset[]>()!;
            var descriptor = manifest["CatalogVariants"]?.Deserialize<CatalogVariant[]>()?.Single(v => v.Variant == variant);
            var atlasAsset = descriptor?.Atlas ?? assets.Single(a => a.Asset.Type == "TextAsset" &&
                a.Sha256 == atlasHash && a.Asset.Name == atlas);
            var family = Path.GetDirectoryName(atlasAsset.LogicalPath);
            var originals = manifest["OriginalFiles"]!.Deserialize<SourceDigest[]>()!.ToDictionary(x => x.BundleId, x => x.Sha256);
            var skeletonAsset = descriptor?.Skeleton ?? assets.Single(a => a.Asset.Type == "TextAsset" &&
                a.Sha256 == ProjectStore.HashBytes(originalSkeleton) && a.Asset.Name == Path.GetFileNameWithoutExtension(skeleton));
            var pageAssets = gameAtlas.Pages.Keys.ToDictionary(name => name, name =>
                descriptor?.Pages.SingleOrDefault(a => a.Asset.Name == Path.GetFileNameWithoutExtension(name)) ??
                assets.Single(a => a.Asset.Type == "Texture2D" &&
                    a.Asset.Name == Path.GetFileNameWithoutExtension(name) &&
                    Path.GetDirectoryName(a.LogicalPath) == family && a.Sha256 == pageHashes[name]));
            var bundleIds = new[] { skeletonAsset.BundleId, atlasAsset.BundleId }
                .Concat(pageAssets.Values.Select(asset => asset.BundleId));
            var bundleEdits = new List<GameReplacementExporter.Edit>();
            var compatible = new HashSet<GameReplacementExporter.Locator>();
            var inputDimensions = new Dictionary<string, (int Width, int Height)>();
            foreach (var (name, page) in inputAtlas.Pages)
            {
                var image = ProjectStore.Under(imageFolder, name);
                if (!File.Exists(image))
                {
                    if (!sameLayout) throw new FileNotFoundException("编辑后的 atlas 缺少同目录贴图：" + name);
                    inputDimensions.Add(name, dimensions[name]);
                    continue;
                }
                var size = RigbyExport.TextureSize(image);
                if (size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192)
                    throw new InvalidDataException($"贴图「{name}」的宽高需在 1 至 8192 像素之间。");
                inputDimensions.Add(name, size);
                if (ProjectStore.Hash(image) == pageHashes[name]) continue;
                var locator = Locate(pageAssets[name]);
                var path = ProjectStore.Under(work, name);
                using var pixels = Image.Load<Rgba32>(image);
                var inputPma = page.GetValueOrDefault("pma") == "true";
                var gamePma = gameAtlas.Pages[name].GetValueOrDefault("pma") == "true";
                if (inputPma != gamePma)
                {
                    if (gamePma) Atlas.ToPma(pixels);
                    else Atlas.ToStraight(pixels);
                }
                JsonFile.Png(path, pixels);
                bundleEdits.Add(new(locator, path, null));
                if (size == dimensions[name]) compatible.Add(locator);
            }

            if (!sameLayout || inputDimensions.Any(page => page.Value != dimensions[page.Key]))
            {
                var outputAtlas = Atlas.Parse(Encoding.UTF8.GetString(RigbyExport.NormalizeAtlas(editedAtlas, inputDimensions)));
                foreach (var (name, page) in outputAtlas.Pages)
                {
                    var size = inputDimensions[name];
                    page["size"] = $"{size.Width},{size.Height}";
                    if (gameAtlas.Pages[name].TryGetValue("pma", out var pma)) page["pma"] = pma;
                    else page.Remove("pma");
                }
                CheckAtlasBounds(outputAtlas, inputDimensions);
                SummonSkeleton.CheckRegions(model, outputAtlas);
                bundleEdits.Add(new(Locate(atlasAsset), null, WriteAtlas(outputAtlas)));
            }
            else if (!editedSkeleton.AsSpan().SequenceEqual(originalSkeleton))
                SummonSkeleton.CheckRegions(model, gameAtlas);

            if (!editedSkeleton.AsSpan().SequenceEqual(originalSkeleton))
                bundleEdits.Add(new(Locate(skeletonAsset), null, editedSkeleton));
            var label = Path.GetFileName(Path.GetDirectoryName(variant));
            var nameZip = BulkLibrary.SafeComponent($"{card.Id}-{card.Name}-{label}-召唤替换") + ".zip";
            return (PackBundles(work, settings, originals, bundleIds, bundleEdits, compatible), nameZip, repairs);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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
        return PackBundles(work, settings, hashes, ids, [], []);
    }

    private static string GameBundlePath(GameSettings settings, string id) =>
        ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}/0000/{id[..2]}/{id}");

    private static FileStream PackBundles(string work, GameSettings settings, Dictionary<string, string> originals,
        IEnumerable<string> bundleIds, List<GameReplacementExporter.Edit> edits, HashSet<GameReplacementExporter.Locator> compatible)
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
                    var replacement = backup;
                    if (byBundle.Contains(id))
                    {
                        replacement = Path.Combine(work, id + ".replacement");
                        File.Copy(backup, replacement);
                        var group = byBundle[id];
                        var originalFormat = group.Where(edit => compatible.Contains(edit.Asset)).ToArray();
                        if (originalFormat.Length > 0) CompatibleTexture.Rewrite(replacement, originalFormat);
                        var otherEdits = group.Where(edit => !compatible.Contains(edit.Asset)).ToArray();
                        if (otherEdits.Length > 0) GameReplacementExporter.Rewrite(replacement, otherEdits);
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
