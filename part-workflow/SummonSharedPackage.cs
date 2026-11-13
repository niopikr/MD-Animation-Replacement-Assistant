using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AssetsTools.NET.Extra;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

internal static class SummonSharedPackage
{
    internal const string BundleId = "294d714a";
    private static readonly string[] Bundles = ["2caa2654", "d14c1c69", BundleId];

    internal static bool Applies(int cardId, string variant) =>
        cardId == 14409 && variant is @"模型\tcg-高清\model.config.json" or @"模型\tcg-低清\model.config.json";

    internal static (FileStream Content, string FileName, List<string> Repairs) Pack(
        string workspace, GameSettings settings, CardChoice card, string variant, string source,
        string skeletonFile, string atlas, string folder, SummonSourceMap? mapping)
    {
        var game = ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}");
        var sharedPath = ProjectStore.Under(game, $"0000/29/{BundleId}");
        if (!File.Exists(sharedPath)) throw new FileNotFoundException("缺少 14409 的共享召唤资源，请先在游戏内下载这张卡的资源。");
        var sharedHash = ProjectStore.Hash(sharedPath);
        var (originalSkeleton, originalAtlas) = ReadShared(sharedPath);
        if (mapping?.SourceBundle is { } bundle && bundle != BundleId)
            throw new InvalidDataException("召唤素材的来源与所选资源不匹配，请重新导出。");
        var nested = Path.Combine(folder, "Rigby2D导出");
        var input = File.Exists(Path.Combine(nested, atlas)) ? nested : folder;
        var atlasPath = Path.Combine(input, atlas);
        var suppliedAtlas = File.Exists(atlasPath) ? File.ReadAllBytes(atlasPath) : null;
        var (localAtlas, localNormalized) = ReadLayout(source, atlas);
        var usesLocalLayout = suppliedAtlas is not null &&
            (suppliedAtlas.AsSpan().SequenceEqual(localAtlas) || suppliedAtlas.AsSpan().SequenceEqual(localNormalized));
        using var shared = mapping?.SourceBundle == BundleId || mapping is null && !usesLocalLayout
            ? OpenExportSource(workspace, settings, variant, forceShared: mapping?.SourceBundle == BundleId)
            : null;
        var reference = shared?.Folder ?? source;
        var (referenceAtlas, normalized) = shared is null ? (localAtlas, localNormalized) : ReadLayout(reference, atlas);
        var skeletonName = Path.ChangeExtension(atlas, ".json");
        var skeletonPath = new[] { Path.Combine(nested, skeletonName), Path.Combine(folder, skeletonName) }.FirstOrDefault(File.Exists);
        var inputSkeleton = skeletonPath is null ? null : File.ReadAllBytes(skeletonPath);
        var skeletonChanged = inputSkeleton is not null && !inputSkeleton.AsSpan().SequenceEqual(originalSkeleton) &&
            !ProjectStore.HashBytes(inputSkeleton).Equals(
                mapping?.SkeletonSha256 ?? ProjectStore.Hash(ProjectStore.Under(reference, shared is null ? skeletonFile : "P14409.json")),
                StringComparison.OrdinalIgnoreCase);
        var inputAtlas = suppliedAtlas ?? normalized;
        var sameLayout = inputAtlas.AsSpan().SequenceEqual(referenceAtlas) || inputAtlas.AsSpan().SequenceEqual(normalized) ||
            ProjectStore.HashBytes(inputAtlas).Equals(mapping?.AtlasSha256, StringComparison.OrdinalIgnoreCase);
        var pages = Atlas.Parse(Encoding.UTF8.GetString(inputAtlas));
        if (pages.Pages.Count == 0 || pages.Pages.Keys.Any(name => name is not ("P14409.png" or "P14409_2.png")))
            throw new InvalidDataException("14409 图集页应沿用 P14409.png 和 P14409_2.png。");
        var inputImages = pages.Pages.Keys.Where(name => File.Exists(ProjectStore.Under(input, name))).ToArray();
        if (skeletonPath is null && suppliedAtlas is null && inputImages.Length == 0)
            throw new InvalidDataException("找不到 14409 的骨架 JSON、atlas 或 PNG。");
        var textureChanged = !sameLayout || inputImages.Any(name =>
        {
            var originalPath = ProjectStore.Under(reference, name);
            var originalHash = mapping?.Pages?.GetValueOrDefault(name) ??
                (File.Exists(originalPath) ? ProjectStore.Hash(originalPath) : null);
            return !ProjectStore.Hash(ProjectStore.Under(input, name)).Equals(originalHash, StringComparison.OrdinalIgnoreCase);
        });
        var work = Path.Combine(AppEnvironment.Runtime(workspace), $"summon-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var output = work + ".zip";
        var label = Path.GetFileName(Path.GetDirectoryName(variant));
        var fileName = BulkLibrary.SafeComponent($"{card.Id}-{card.Name}-{label}-召唤替换") + ".zip";
        (FileStream Content, string FileName, List<string> Repairs) CopyOriginals() =>
            (SummonPackage.PackCurrentCopies(work, settings, Bundles, BundleId, sharedHash), fileName, []);
        try
        {
            if (!skeletonChanged && !textureChanged) return CopyOriginals();
            var (skeleton, model, repairs) = SummonSkeleton.Prepare(skeletonChanged ? inputSkeleton! : originalSkeleton, originalSkeleton);
            File.WriteAllBytes(Path.Combine(work, "P14409.json"), skeleton);
            if (textureChanged)
            {
                var sizes = new Dictionary<string, (int Width, int Height)>();
                foreach (var (name, page) in pages.Pages)
                {
                    var image = ProjectStore.Under(input, name);
                    if (!File.Exists(image))
                    {
                        if (!sameLayout) throw new FileNotFoundException("编辑后的 atlas 缺少同目录贴图：" + name);
                        image = ProjectStore.Under(reference, name);
                        if (!File.Exists(image) || mapping?.Pages?.GetValueOrDefault(name) is { } hash &&
                            !ProjectStore.Hash(image).Equals(hash, StringComparison.OrdinalIgnoreCase))
                            throw new FileNotFoundException("请提供同一次导出的贴图页：" + name);
                    }
                    var size = RigbyExport.TextureSize(image);
                    if (size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192)
                        throw new InvalidDataException($"贴图「{name}」的宽高需在 1 至 8192 像素之间。");
                    sizes.Add(name, size);
                    using var pixels = Image.Load<Rgba32>(image);
                    if (page.GetValueOrDefault("pma") != "true") Atlas.ToPma(pixels);
                    JsonFile.Png(ProjectStore.Under(work, name), pixels);
                }
                var prepared = Atlas.Parse(Encoding.UTF8.GetString(RigbyExport.NormalizeAtlas(inputAtlas, sizes)));
                foreach (var (name, page) in prepared.Pages)
                {
                    page["size"] = $"{sizes[name].Width},{sizes[name].Height}";
                    page["pma"] = "true";
                }
                SummonPackage.CheckAtlasBounds(prepared, sizes);
                SummonSkeleton.CheckRegions(model, prepared);
                File.WriteAllBytes(Path.Combine(work, "P14409.atlas"), SummonPackage.WriteAtlas(prepared));
            }
            else
                SummonSkeleton.CheckRegions(model, Atlas.Parse(Encoding.UTF8.GetString(originalAtlas)));

            if (!Run(workspace, game, sharedHash, work, output)) return CopyOriginals();
            if (!File.Exists(output)) throw new InvalidDataException("未生成共享召唤替换包。");
            var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.DeleteOnClose);
            try
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var entries = Bundles.SelectMany(id => new[] { "原始", "替换" }
                        .Select(group => $"{group}/0000/{id[..2]}/{id}")).ToHashSet();
                    if (zip.Entries.Count != entries.Count || zip.Entries.Any(e => e.Length == 0 || !entries.Remove(e.FullName)))
                        throw new InvalidDataException("共享召唤替换包缺少关联资源。");
                }
                stream.Position = 0;
                return (stream, fileName, repairs);
            }
            catch { stream.Dispose(); throw; }
        }
        catch { File.Delete(output); throw; }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static (byte[] Original, byte[] Normalized) ReadLayout(string folder, string atlas)
    {
        var original = File.ReadAllBytes(ProjectStore.Under(folder, atlas));
        var pages = Atlas.Parse(Encoding.UTF8.GetString(original)).Pages;
        var sizes = pages.Keys.ToDictionary(name => name, name => RigbyExport.TextureSize(ProjectStore.Under(folder, name)));
        return (original, RigbyExport.NormalizeAtlas(original, sizes));
    }

    internal sealed class ExportSource(string folder) : IDisposable
    {
        public string Folder { get; } = folder;
        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }

    internal static ExportSource? OpenExportSource(string workspace, GameSettings settings, string variant, bool forceShared = false)
    {
        if (string.IsNullOrWhiteSpace(settings.GameRoot) || string.IsNullOrWhiteSpace(settings.Account)) return null;
        var game = ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}");
        var folder = Path.Combine(AppEnvironment.Runtime(workspace), $"summon-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var source = new ExportSource(folder);
        try
        {
            var model = variant.Contains("tcg-高清", StringComparison.Ordinal) ? "2caa2654" : "d14c1c69";
            if (Run(workspace, "--extract", game, model, folder, forceShared ? "shared" : "active")) return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
        source.Dispose();
        return null;
    }

    private static (byte[] Skeleton, byte[] Atlas) ReadShared(string path)
    {
        var manager = AssetIndex.Manager();
        try
        {
            var bundle = manager.LoadBundleFile(path, true);
            var cab = manager.LoadAssetsFileFromBundle(bundle, 0, false);
            manager.LoadClassDatabaseFromPackage(cab.file.Metadata.UnityVersion);
            byte[] Read(long id, string name)
            {
                var info = cab.file.GetAssetInfo(id);
                if (info is null || info.TypeId != (int)AssetClassID.TextAsset)
                    throw new InvalidDataException($"共享召唤资源找不到「{name}」，请确认游戏资源版本。");
                var data = manager.GetBaseField(cab, info);
                if (data["m_Name"].AsString != name)
                    throw new InvalidDataException($"共享召唤资源「{name}」的位置已变化。");
                return AssetIndex.Script(data);
            }
            return (Read(-1845077364187591291, "P14409JS"), Read(5324393166265416535, "P14409.atlas"));
        }
        finally { manager.UnloadAll(); }
    }

    private static bool Run(string workspace, params string[] arguments)
    {
        var runtime = AppEnvironment.Resource("effect-runtime");
        var start = new ProcessStartInfo(Path.Combine(runtime, "python.exe"))
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-B", "-X", "utf8", Path.Combine(runtime, "pack_shared_summon.py") }.Concat(arguments))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动共享召唤打包。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.WaitForExit();
            throw new TimeoutException("共享召唤资源打包超时，请重试。");
        }
        var message = stdout.GetAwaiter().GetResult().Trim();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode == 2 && message.Length > 0) throw new InvalidDataException(message);
        if (process.ExitCode == 0) return true;
        if (process.ExitCode == 3) return false;
        File.AppendAllText(Path.Combine(AppEnvironment.Runtime(workspace), "召唤打包.log"), error + Environment.NewLine);
        throw new InvalidDataException("共享召唤资源校验失败。请确认高清、低清及共享资源都已下载，详情见召唤打包日志。");
    }
}
