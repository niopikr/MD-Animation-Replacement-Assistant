using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

internal static class SummonAtlasTransfer
{
    internal static Atlas Prepare(string work, SummonTarget target, byte[] original, Atlas input,
        Dictionary<string, string> images, Dictionary<string, (int Width, int Height)> sizes,
        HashSet<string>? changedPages,
        Dictionary<GameReplacementExporter.Locator, GameReplacementExporter.Edit> edits)
    {
        var game = Atlas.Parse(Encoding.UTF8.GetString(original));
        var names = game.Pages.Keys.ToArray();
        var locators = names.Select(name => target.Pages.Single(page =>
            page.Name.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))).ToArray();
        var dimensions = names.Zip(locators).ToDictionary(pair => pair.First, pair =>
        {
            var size = target.Dimensions[Array.IndexOf(target.Pages, pair.Second)];
            return (size[0], size[1]);
        });
        var keepLayout = changedPages is not null && SameLayout(game, input, dimensions, sizes);
        if (input.Pages.Count > names.Length)
            return Combine(work, target, game, names[0], locators[0], input, images, sizes, edits);

        var mapped = input.Pages.Keys.ToDictionary(name => name,
            name => names.Contains(name) ? name : "");
        var free = new Queue<string>(names.Except(mapped.Values));
        foreach (var name in mapped.Keys.ToArray())
            if (mapped[name].Length == 0) mapped[name] = free.Dequeue();

        var output = new Atlas();
        foreach (var name in names)
        {
            var entry = mapped.SingleOrDefault(x => x.Value == name);
            var fields = new Dictionary<string, string>(game.Pages[name]);
            output.Pages.Add(name, fields);
            if (entry.Key is not { } from) continue;
            if (keepLayout && !changedPages!.Contains(from)) continue;
            var size = sizes[from];
            fields["size"] = $"{size.Width},{size.Height}";
            var locator = locators[Array.IndexOf(names, name)];
            var path = PreparedPng(work, locator, images[from], Pma(input.Pages[from]), Pma(fields));
            SummonPackage.AddEdit(edits, new(locator, path, null));
        }
        if (keepLayout) return game;
        foreach (var region in input.Regions.Values)
            output.Regions.Add(region.Name, new Atlas.Region
            {
                Name = region.Name, Page = mapped[region.Page], Fields = new(region.Fields)
            });
        return Finish(target, original, output, edits);
    }

    private static bool SameLayout(Atlas game, Atlas input, Dictionary<string, (int, int)> dimensions,
        Dictionary<string, (int Width, int Height)> sizes)
    {
        if (!input.Pages.Keys.SequenceEqual(game.Pages.Keys) || input.Regions.Count != game.Regions.Count ||
            sizes.Any(x => dimensions[x.Key] != x.Value))
            return false;
        var normalized = Atlas.Parse(Encoding.UTF8.GetString(RigbyExport.NormalizeAtlas(SummonPackage.WriteAtlas(game), dimensions)));
        return input.Regions.Values.All(region => normalized.Regions.TryGetValue(region.Name, out var other) &&
            region.Page == other.Page && region.Fields.Count == other.Fields.Count &&
            region.Fields.All(field => other.Fields.GetValueOrDefault(field.Key) == field.Value));
    }

    private static Atlas Combine(string work, SummonTarget target, Atlas game, string pageName,
        GameReplacementExporter.Locator locator, Atlas input, Dictionary<string, string> images,
        Dictionary<string, (int Width, int Height)> sizes, Dictionary<GameReplacementExporter.Locator, GameReplacementExporter.Edit> edits)
    {
        const int gap = 4;
        var horizontal = (Width: sizes.Values.Sum(s => s.Width) + gap * (sizes.Count - 1), Height: sizes.Values.Max(s => s.Height));
        var vertical = (Width: sizes.Values.Max(s => s.Width), Height: sizes.Values.Sum(s => s.Height) + gap * (sizes.Count - 1));
        var across = horizontal.Width <= 8192 && (vertical.Height > 8192 ||
            (long)horizontal.Width * horizontal.Height <= (long)vertical.Width * vertical.Height);
        var size = across ? horizontal : vertical;
        if (size.Width > 8192 || size.Height > 8192)
            throw new InvalidDataException("同步到贴图页较少的画质时，合并图集超过 8192 像素。请缩小图集后再导出。");
        var output = new Atlas();
        foreach (var (name, fields) in game.Pages)
            output.Pages.Add(name, new(fields));
        output.Pages[pageName]["size"] = $"{size.Width},{size.Height}";
        using var canvas = new Image<Rgba32>(size.Width, size.Height);
        var x = 0;
        var y = 0;
        foreach (var (name, fields) in input.Pages)
        {
            using var page = Image.Load<Rgba32>(images[name]);
            ConvertAlpha(page, Pma(fields), Pma(output.Pages[pageName]));
            page.ProcessPixelRows(canvas, (source, destination) =>
            {
                for (var row = 0; row < page.Height; row++)
                    source.GetRowSpan(row).CopyTo(destination.GetRowSpan(y + row)[x..]);
            });
            foreach (var region in input.Regions.Values.Where(region => region.Page == name))
            {
                var copy = new Atlas.Region { Name = region.Name, Page = pageName, Fields = new(region.Fields) };
                var field = copy.Fields.ContainsKey("bounds") ? "bounds" : "xy";
                var values = copy.Fields[field].Split(',').Select(int.Parse).ToArray();
                values[0] += x;
                values[1] += y;
                copy.Fields[field] = string.Join(',', values);
                output.Regions.Add(copy.Name, copy);
            }
            if (across) x += page.Width + gap;
            else y += page.Height + gap;
        }
        var path = Path.Combine(work, $"page-{Guid.NewGuid():N}.png");
        JsonFile.Png(path, canvas);
        SummonPackage.AddEdit(edits, new(locator, path, null));
        return Finish(target, SummonPackage.WriteAtlas(game), output, edits);
    }

    private static Atlas Finish(SummonTarget target, byte[] baseline, Atlas output,
        Dictionary<GameReplacementExporter.Locator, GameReplacementExporter.Edit> edits)
    {
        var updated = SummonPackage.WriteAtlas(output);
        if (!updated.AsSpan().SequenceEqual(baseline))
            SummonPackage.AddEdit(edits, new(target.Atlas, null, updated));
        return output;
    }

    private static bool Pma(Dictionary<string, string> fields) => fields.GetValueOrDefault("pma") == "true";

    private static void ConvertAlpha(Image<Rgba32> image, bool input, bool output)
    {
        if (input == output) return;
        if (output) Atlas.ToPma(image);
        else Atlas.ToStraight(image);
    }

    private static string PreparedPng(string work, GameReplacementExporter.Locator locator, string source, bool input, bool output)
    {
        if (input == output) return source;
        using var pixels = Image.Load<Rgba32>(source);
        ConvertAlpha(pixels, input, output);
        var path = Path.Combine(work, $"{locator.BundleId}-{Guid.NewGuid():N}.png");
        JsonFile.Png(path, pixels);
        return path;
    }
}
