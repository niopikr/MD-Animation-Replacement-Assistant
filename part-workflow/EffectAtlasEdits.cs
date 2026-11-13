using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

internal static class EffectAtlasEdits
{
    internal static Dictionary<string, string> Prepare(string folder, string work, EffectPage[] pages)
        => Prepare(folder, work, pages.ToDictionary(p => p.File, p => (p.Width, p.Height), StringComparer.OrdinalIgnoreCase));

    internal static Dictionary<string, string> Prepare(string folder, string work, Dictionary<string, (int Width, int Height)> pageSizes)
    {
        var rigbyFolder = Path.Combine(folder, "Rigby2D导出");
        if (!Directory.Exists(rigbyFolder)) return new(StringComparer.OrdinalIgnoreCase);
        var atlases = Directory.GetFiles(rigbyFolder, "*.atlas");
        if (atlases.Length != 1)
            throw new InvalidDataException("请在「Rigby2D导出」放一份 JSON + Atlas 导出的图集。");
        var editedPath = atlases[0];
        var originalPath = Path.Combine(folder, Path.GetFileName(editedPath));
        if (!File.Exists(originalPath))
            throw new InvalidDataException("Rigby2D 导出的 atlas 请沿用原模型的文件名：" + Path.GetFileName(editedPath));

        var original = Atlas.Parse(File.ReadAllText(originalPath));
        var edited = Atlas.Parse(File.ReadAllText(editedPath));
        var sources = new Dictionary<string, Image<Rgba32>>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, Image<Rgba32>>(StringComparer.OrdinalIgnoreCase);
        var destinations = new Dictionary<string, Image<Rgba32>>(StringComparer.OrdinalIgnoreCase);
        var occupied = new Dictionary<string, List<(Rectangle Area, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        try
        {
            foreach (var name in original.Regions.Keys)
            {
                if (!edited.Regions.ContainsKey(name)) continue;
                var before = original.MakePart(0, name, name, new JsonObject());
                var after = edited.MakePart(0, name, name, new JsonObject());
                if (!pageSizes.TryGetValue(before.Page, out var page))
                    throw new InvalidDataException("原图集包含未映射的贴图页：" + before.Page);
                var originalPage = LoadPage(sources, folder, before.Page, page);
                var modifiedPage = LoadPage(inputs, rigbyFolder, after.Page, null);
                original.FitToTexture(before, originalPage.Width, originalPage.Height);
                edited.FitToTexture(after, modifiedPage.Width, modifiedPage.Height);
                if ((before.Width, before.Height) != (after.Width, after.Height))
                    throw new InvalidDataException("Rigby2D 部件画布尺寸改变，请保持原尺寸：" + name);
                using var oldPixels = Atlas.Extract(originalPage, before);
                using var newPixels = Atlas.Extract(modifiedPage, after);
                if (after.Pma != before.Pma)
                {
                    if (before.Pma) Atlas.ToPma(newPixels);
                    else Atlas.ToStraight(newPixels);
                }
                count++;
                if (SameVisiblePixels(oldPixels, newPixels)) continue;
                using var packed = Atlas.Pack(newPixels, before);
                if (!destinations.TryGetValue(before.Page, out var destination))
                {
                    destinations[before.Page] = destination = originalPage.Clone();
                    occupied[before.Page] = [];
                }
                var area = new Rectangle(before.Bounds[0], before.Bounds[1], packed.Width, packed.Height);
                foreach (var previous in occupied[before.Page])
                {
                    var intersection = Rectangle.Intersect(area, previous.Area);
                    for (var y = intersection.Top; y < intersection.Bottom; y++)
                        for (var x = intersection.Left; x < intersection.Right; x++)
                            if (destination[x, y] != packed[x - area.X, y - area.Y])
                                throw new InvalidDataException($"两个修改后的部件使用同一块贴图且像素冲突：{previous.Name} / {name}");
                }
                for (var y = 0; y < packed.Height; y++)
                    for (var x = 0; x < packed.Width; x++)
                        destination[x + area.X, y + area.Y] = packed[x, y];
                occupied[before.Page].Add((area, name));
            }
            if (count == 0) throw new InvalidDataException("Rigby2D 图集没有可对应的部件。请保留原图片资源名。");
            foreach (var (name, page) in destinations)
            {
                var path = ProjectStore.Under(work, name);
                JsonFile.Png(path, page);
                result.Add(name, path);
            }
            return result;
        }
        finally
        {
            foreach (var image in sources.Values.Concat(inputs.Values).Concat(destinations.Values)) image.Dispose();
        }
    }

    private static Image<Rgba32> LoadPage(Dictionary<string, Image<Rgba32>> images, string folder, string name, (int Width, int Height)? page)
    {
        if (images.TryGetValue(name, out var existing)) return existing;
        var path = ProjectStore.Under(folder, name);
        var info = Image.Identify(path);
        if (info.Width is < 1 or > 8192 || info.Height is < 1 or > 8192 ||
            (page is { } size && (info.Width != size.Width || info.Height != size.Height)))
            throw new InvalidDataException("图集 PNG 尺寸不匹配：" + name);
        return images[name] = Image.Load<Rgba32>(path);
    }

    private static bool SameVisiblePixels(Image<Rgba32> a, Image<Rgba32> b)
    {
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var p = a[x, y];
                var q = b[x, y];
                if (p.A != q.A) return false;
                if (p.A != 0 && (Math.Abs(p.R-q.R) > 1 || Math.Abs(p.G-q.G) > 1 || Math.Abs(p.B-q.B) > 1))
                    return false;
            }
        return true;
    }
}
