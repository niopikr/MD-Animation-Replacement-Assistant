using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
    public static void Png(string path, Image<Rgba32> image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        // RGB source PNG metadata survives masking/cropping. The default encoder can then
        // discard newly created alpha, making the rectangular background opaque again.
        image.SaveAsPng(temporary, new SixLabors.ImageSharp.Formats.Png.PngEncoder {
            ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha,
            BitDepth = SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8 });
        File.Move(temporary, path, true);
    }
}

public sealed class ProjectState
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public int CardId { get; set; }
    public string ChineseName { get; set; } = "";
    public string Variant { get; set; } = "";
    public string SpineVersion { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string SkeletonFile { get; set; } = "";
    public string AtlasFile { get; set; } = "";
    public string Artwork { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public int CurrentPart { get; set; }
    public List<Part> Parts { get; set; } = [];
    public Dictionary<string, string> SourceHashes { get; set; } = [];
    public List<string> Exports { get; set; } = [];
}

public sealed class Part
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Slot { get; set; } = "";
    public string RegionName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Page { get; set; } = "";
    public int[] Bounds { get; set; } = [];
    public int[] Offsets { get; set; } = [];
    public int[] AtlasBounds { get; set; } = [];
    public int[] AtlasOffsets { get; set; } = [];
    public double PageScaleX { get; set; } = 1;
    public double PageScaleY { get; set; } = 1;
    public int Rotation { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Pma { get; set; }
    public float[] Uvs { get; set; } = [];
    public int[] Triangles { get; set; } = [];
    public string Status { get; set; } = "pending";
    public string ConfirmedRevision { get; set; } = "";
    public List<string> Revisions { get; set; } = [];
    public string DraftMask { get; set; } = "";
    public string DraftCutout { get; set; } = "";
    public Placement Placement { get; set; } = new();
    public string Key => Number.ToString("D3");
}

public sealed class Placement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; }
    public bool Flip { get; set; }
}

public sealed class Revision
{
    public string Kind { get; set; } = "replaced";
    public string Mask { get; set; } = "";
    public string Cutout { get; set; } = "";
    public string Aligned { get; set; } = "";
    public Placement Placement { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed record CardChoice(int Id, string Name, string VersionFolder, string[] Variants);
public sealed record NewProjectRequest(int CardId, string Variant);
public sealed record SelectionRequest(string Mask);
public sealed record ConfirmRequest(string Image, Placement Placement);
public sealed record SamRequest(string Mask, float[][] Points, bool LimitToHint = false);
public sealed record StepRequest(int Number);
public sealed record RestoreRequest(string Revision);

public static class PartLabels
{
    public static string Get(string name) => name switch
    {
        "Head" => "头部", "HeadHairA" => "额前头发 A", "HeadHairB" => "头发 B",
        "HeadParts" => "头部配件", "Eye_0" => "眼睛状态 0", "Eye_1" => "眼睛状态 1",
        "Hair" => "长发主体", "LeftUpperArm" => "左上臂", "RightArm" => "右臂",
        "LeftArm" => "左臂组合", "ArmorCloth" => "装甲与布料", "Body" => "身体主体",
        "CharmA" => "挂饰 A", "CharmABackA" => "挂饰 A 后片 1", "CharmABackB" => "挂饰 A 后片 2",
        "CharmABackC" => "挂饰 A 后片 3", "CharmB" => "挂饰 B", "CharmBBackA" => "挂饰 B 后片 1",
        "CharmBBackB" => "挂饰 B 后片 2", "LeftKnee" => "左膝", "RightKnee" => "右膝",
        "ShoulderArmor" => "肩甲", "WaistPartsA" => "腰部配件 A", "WaistPartsB" => "腰部配件 B",
        "WaistPartsC" => "腰部配件 C", _ => name
    };
}
