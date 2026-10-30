using System.Globalization;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

public sealed class Atlas
{
    public sealed class Region
    {
        public string Name = "", Page = "";
        public Dictionary<string, string> Fields = [];
    }
    public Dictionary<string, Dictionary<string, string>> Pages { get; } = [];
    public Dictionary<string, Region> Regions { get; } = [];
    public static Atlas Parse(string text)
    {
        var result = new Atlas();
        string? page = null;
        Region? region = null;
        var newPage = true;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('\ufeff');
            if (line.Length == 0) { newPage = true; region = null; continue; }
            var colon = line.IndexOf(':');
            if (colon >= 0)
            {
                var fields = region?.Fields ?? (page is null ? throw new InvalidDataException("Atlas 缺少页名") : result.Pages[page]);
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            else if (newPage)
            {
                if (Path.GetFileName(line) != line) throw new InvalidDataException("不支持带子目录的图集页名");
                page = line;
                result.Pages.Add(page, []);
                region = null;
                newPage = false;
            }
            else
            {
                region = new Region { Name = line, Page = page! };
                result.Regions.Add(line, region);
            }
        }
        return result;
    }
    static int[] Ints(string s) => s.Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
    public Part MakePart(int number, string slot, string name, JsonObject attachment)
    {
        var path = attachment["path"]?.GetValue<string>() ?? name;
        if (!Regions.TryGetValue(path, out var r)) throw new InvalidDataException($"图集缺少部件 {path}");
        var f = r.Fields;
        var bounds = f.TryGetValue("bounds", out var b) ? Ints(b) : Ints(f["xy"]).Concat(Ints(f["size"])).ToArray();
        var offsets = f.TryGetValue("offsets", out var o) ? Ints(o) :
            (f.TryGetValue("offset", out var off) ? Ints(off) : [0, 0]).Concat(f.TryGetValue("orig", out var orig) ? Ints(orig) : bounds.Skip(2)).ToArray();
        var rotation = f.GetValueOrDefault("rotate", "0");
        var degrees = rotation switch { "true" => 90, "false" => 0, _ => int.Parse(rotation, CultureInfo.InvariantCulture) };
        if (degrees is not (0 or 90 or 180 or 270)) throw new InvalidDataException("不支持的 Atlas 旋转");
        if (bounds.Length != 4 || offsets.Length != 4 || offsets[2] <= 0 || offsets[3] <= 0 || offsets[2] > 8192 || offsets[3] > 8192)
            throw new InvalidDataException("部件画布尺寸无效");
        return new Part
        {
            Number = number, Name = name, Label = PartLabels.Get(name), Slot = slot, RegionName = path,
            Type = attachment["type"]?.GetValue<string>() ?? "region", Page = r.Page,
            Bounds = bounds, Offsets = offsets, Rotation = degrees, Width = offsets[2], Height = offsets[3],
            Pma = Pages[r.Page].GetValueOrDefault("pma", "false") == "true",
            Uvs = attachment["uvs"]?.AsArray().Select(v => v!.GetValue<float>()).ToArray() ?? [],
            Triangles = attachment["triangles"]?.AsArray().Select(v => v!.GetValue<int>()).ToArray() ?? []
        };
    }
    public void FitToTexture(Part p, int textureWidth, int textureHeight)
    {
        p.AtlasBounds = (int[])p.Bounds.Clone(); p.AtlasOffsets = (int[])p.Offsets.Clone();
        var logicalSize = Pages[p.Page].TryGetValue("size", out var size) ? Ints(size) : [textureWidth, textureHeight];
        var sx = (double)textureWidth / logicalSize[0]; var sy = (double)textureHeight / logicalSize[1];
        p.PageScaleX = sx; p.PageScaleY = sy;
        if (sx == 1 && sy == 1) return;
        static int Round(double n) => (int)Math.Round(n, MidpointRounding.AwayFromZero);
        var b = p.Bounds; var swap = p.Rotation % 180 != 0;
        var x = Round(b[0] * sx); var y = Round(b[1] * sy);
        var storedWidth = Round((b[0] + (swap ? b[3] : b[2])) * sx) - x;
        var storedHeight = Round((b[1] + (swap ? b[2] : b[3])) * sy) - y;
        var width = swap ? storedHeight : storedWidth; var height = swap ? storedWidth : storedHeight;
        var ux = swap ? sy : sx; var uy = swap ? sx : sy;
        var left = Round(p.Offsets[0] * ux); var bottom = Round(p.Offsets[1] * uy);
        p.Width = Math.Max(Round(p.Width * ux), left + width);
        p.Height = Math.Max(Round(p.Height * uy), bottom + height);
        p.Bounds = [x, y, width, height]; p.Offsets = [left, bottom, p.Width, p.Height];
    }
    public static Image<Rgba32> Extract(Image<Rgba32> page, Part p)
    {
        var b = p.Bounds;
        var swap = p.Rotation % 180 != 0;
        using var crop = page.Clone(x => x.Crop(new Rectangle(b[0], b[1], swap ? b[3] : b[2], swap ? b[2] : b[3])));
        if (p.Rotation != 0) crop.Mutate(x => x.Rotate(p.Rotation));
        var result = new Image<Rgba32>(p.Width, p.Height);
        var left = p.Offsets[0]; var top = p.Height - b[3] - p.Offsets[1];
        for (var y = 0; y < crop.Height; y++)
            for (var x = 0; x < crop.Width; x++) result[x + left, y + top] = crop[x, y];
        return result;
    }
    public static Image<Rgba32> Pack(Image<Rgba32> canvas, Part p)
    {
        var b = p.Bounds;
        var result = canvas.Clone(x => x.Crop(new Rectangle(p.Offsets[0], p.Height - b[3] - p.Offsets[1], b[2], b[3])));
        if (p.Rotation != 0) result.Mutate(x => x.Rotate((360 - p.Rotation) % 360));
        return result;
    }
    public static void ToStraight(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var c = ref row[x];
                    if (c.A == 0) { c = default; continue; }
                    c.R = (byte)Math.Min(255, (c.R * 255 + c.A / 2) / c.A);
                    c.G = (byte)Math.Min(255, (c.G * 255 + c.A / 2) / c.A);
                    c.B = (byte)Math.Min(255, (c.B * 255 + c.A / 2) / c.A);
                }
            }
        });
    }
    public static void ToPma(Image<Rgba32> image)
    {
        image.ProcessPixelRows(a =>
        {
            for (var y = 0; y < a.Height; y++)
            {
                var row = a.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var c = ref row[x];
                    c.R = (byte)((c.R * c.A + 127) / 255);
                    c.G = (byte)((c.G * c.A + 127) / 255);
                    c.B = (byte)((c.B * c.A + 127) / 255);
                }
            }
        });
    }
    public static Image<L8> MeshMask(Part p)
    {
        var mask = new Image<L8>(p.Width, p.Height);
        if (p.Triangles.Length == 0)
        {
            for (var y = 0; y < p.Height; y++) for (var x = 0; x < p.Width; x++) mask[x, y] = new L8(255);
            return mask;
        }
        for (var i = 0; i < p.Triangles.Length; i += 3)
        {
            var points = p.Triangles.Skip(i).Take(3).Select(n => (X: p.Uvs[n * 2] * p.Width, Y: p.Uvs[n * 2 + 1] * p.Height)).ToArray();
            static double Cross((float X, float Y) a, (float X, float Y) b, double x, double y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            for (var y = Math.Max(0, (int)Math.Floor(points.Min(v => v.Y))); y < Math.Min(p.Height, Math.Ceiling(points.Max(v => v.Y))); y++)
                for (var x = Math.Max(0, (int)Math.Floor(points.Min(v => v.X))); x < Math.Min(p.Width, Math.Ceiling(points.Max(v => v.X))); x++)
                {
                    var a = Cross(points[0], points[1], x + .5, y + .5);
                    var b = Cross(points[1], points[2], x + .5, y + .5);
                    var c = Cross(points[2], points[0], x + .5, y + .5);
                    if ((a >= -.0001 && b >= -.0001 && c >= -.0001) || (a <= .0001 && b <= .0001 && c <= .0001)) mask[x, y] = new L8(255);
                }
        }
        return mask;
    }
    public static Image<Rgba32> MaskRgba(Image<L8> mask)
    {
        var result = new Image<Rgba32>(mask.Width, mask.Height);
        for (var y = 0; y < mask.Height; y++) for (var x = 0; x < mask.Width; x++) result[x, y] = new Rgba32(255, 255, 255, mask[x, y].PackedValue);
        return result;
    }
}
