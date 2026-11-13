using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardAssetWorkbench;
using SixLabors.ImageSharp;

namespace PartWorkflow;

internal static class RigbyExport
{
    public static (FileStream Content, string FileName) Create(ProjectStore store, NewProjectRequest request)
    {
        var (card, variant, _, _, source, skeleton, atlas) = store.OpenModel(request);
        if (!SummonSharedPackage.Applies(card.Id, variant))
            return CreateZip(store, card, variant, source, skeleton, atlas, null);
        var settings = new InstallSettings(store.Workspace).Read();
        using var shared = SummonSharedPackage.OpenExportSource(store.Workspace, settings, variant);
        return shared is null
            ? CreateZip(store, card, variant, source, skeleton, atlas, null)
            : CreateZip(store, card, variant, shared.Folder, "P14409.json", "P14409.atlas", SummonSharedPackage.BundleId);
    }

    private static (FileStream Content, string FileName) CreateZip(ProjectStore store, CardChoice card, string variant,
        string source, string skeleton, string atlas, string? sourceBundle)
    {
        var atlasPath = ProjectStore.Under(source, atlas);
        var originalAtlas = File.ReadAllBytes(atlasPath);
        var pages = Atlas.Parse(Encoding.UTF8.GetString(originalAtlas)).Pages.Keys.ToArray();
        if (pages.Length == 0)
            throw new InvalidDataException("图集没有可导出的 PNG 贴图");

        var stem = Path.GetFileNameWithoutExtension(atlas);
        var skeletonPath = ProjectStore.Under(source, skeleton);
        var files = new List<(string Name, string Path)>();
        foreach (var page in pages)
        {
            if (Path.GetFileName(page) != page)
                throw new InvalidDataException("图集的 PNG 文件名必须位于同一目录");
            var path = ProjectStore.Under(source, page);
            if (!File.Exists(path))
                throw new FileNotFoundException("图集缺少贴图，请先更新卡片目录：" + page);
            files.Add((page, path));
        }
        var dimensions = files.ToDictionary(file => file.Name, file => TextureSize(file.Path));
        var rigbyAtlas = NormalizeAtlas(originalAtlas, dimensions);

        var variantName = Path.GetFileName(Path.GetDirectoryName(variant));
        var fileName = BulkLibrary.SafeComponent($"{card.Id}-{card.Name}-{variantName}-Rigby2D") + ".zip";
        var temporaryPath = Path.Combine(AppEnvironment.Runtime(store.Workspace), $"rigby-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.DeleteOnClose);
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntryFromFile(skeletonPath, stem + ".json", CompressionLevel.Fastest);
                using (var atlasEntry = zip.CreateEntry(stem + ".atlas", CompressionLevel.Fastest).Open())
                    atlasEntry.Write(rigbyAtlas);
                foreach (var file in files)
                    zip.CreateEntryFromFile(file.Path, file.Name, CompressionLevel.NoCompression);
                var mapping = new SummonSourceMap(SummonPackage.MapFormat, card.Id, variant,
                    ProjectStore.HashBytes(rigbyAtlas), ProjectStore.Hash(skeletonPath),
                    files.ToDictionary(file => file.Name, file => ProjectStore.Hash(file.Path)), sourceBundle);
                using var entry = zip.CreateEntry(SummonPackage.MapName, CompressionLevel.Fastest).Open();
                JsonSerializer.Serialize(entry, mapping, JsonFile.Options);
            }
            stream.Position = 0;
            return (stream, fileName);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static (int Width, int Height) TextureSize(string path)
    {
        var info = Image.Identify(path);
        return (info.Width, info.Height);
    }

    internal static byte[] NormalizeAtlas(byte[] original, Dictionary<string, (int Width, int Height)> dimensions)
    {
        var atlas = Atlas.Parse(Encoding.UTF8.GetString(original));
        var scaled = atlas.Pages.Where(page => page.Value.TryGetValue("size", out var size) &&
            size.Split(',').Select(int.Parse).ToArray() is [var width, var height] &&
            (width, height) != dimensions[page.Key]).Select(page => page.Key).ToHashSet();
        if (scaled.Count == 0) return original;

        var text = new StringBuilder();
        var attachment = new JsonObject();
        foreach (var (name, fields) in atlas.Pages)
        {
            var (width, height) = dimensions[name];
            text.AppendLine(name);
            foreach (var (key, value) in fields)
                text.AppendLine(key == "size" && scaled.Contains(name) ? $"size: {width},{height}" : $"{key}: {value}");
            foreach (var region in atlas.Regions.Values.Where(region => region.Page == name))
            {
                text.AppendLine(region.Name);
                if (!scaled.Contains(name))
                {
                    foreach (var (key, value) in region.Fields) text.AppendLine($"  {key}: {value}");
                    continue;
                }
                var part = atlas.MakePart(0, region.Name, region.Name, attachment);
                atlas.FitToTexture(part, width, height);
                text.AppendLine($"  bounds: {string.Join(',', part.Bounds)}");
                text.AppendLine($"  offsets: {string.Join(',', part.Offsets)}");
                foreach (var (key, value) in region.Fields.Where(field => field.Key is not ("bounds" or "offsets" or "xy" or "size" or "orig" or "offset")))
                    text.AppendLine($"  {key}: {value}");
            }
            text.AppendLine();
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
