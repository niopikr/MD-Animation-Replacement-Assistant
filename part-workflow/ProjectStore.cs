using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

public sealed class ProjectStore(string workspace, string? projectRoot = null)
{
    public string Workspace { get; } = workspace;
    public string Root => projectRoot ?? Path.Combine(Workspace, "项目");
    string Library => AppEnvironment.Library(Workspace);
    public object Gate { get; } = new();
    List<CardChoice>? _choices;
    public void InvalidateChoices()=>_choices=null;
    public IReadOnlyList<CardChoice> Choices()
    {
        if (_choices is not null) return _choices;
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(Library, "library-state.json")))!;
        _choices = [];
        foreach (var item in state["Cards"]!.AsObject())
        {
            var c = item.Value!;
            var version = c["VersionFolder"]!.GetValue<string>();
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Library, version, "manifest.json")))!;
            var configs = manifest["Preview"]?["Configurations"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            if (configs.Length > 0) _choices.Add(new CardChoice(c["Id"]!.GetValue<int>(), c["ChineseName"]!.GetValue<string>(), version, configs));
        }
        return _choices.OrderBy(c => c.Name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), false)).ToList();
    }
    public string DirectoryFor(string id)
    {
        if (id != Path.GetFileName(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-z-]{10,90}$")) throw new InvalidDataException("项目编号无效");
        return Path.Combine(Root, id);
    }
    public static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件路径越界");
        return path;
    }
    public string RebaseSourceVersion(string value)
    {
        if(Directory.Exists(value))return value;
        var path=value.Replace('\\','/');
        foreach(var marker in new[]{"/运行数据/素材缓存/","/卡片素材工作区/全动画卡片素材库/"}){var i=path.IndexOf(marker,StringComparison.OrdinalIgnoreCase);if(i>=0)return Under(Workspace,path[(i+1)..]);}
        return value;
    }
    public ProjectState Load(string id){var p=JsonFile.Read<ProjectState>(Path.Combine(DirectoryFor(id),"project.json"));p.SourceVersion=RebaseSourceVersion(p.SourceVersion);return p;}
    public void Save(ProjectState state)
    {
        state.UpdatedUtc = DateTime.UtcNow;
        JsonFile.Write(Path.Combine(DirectoryFor(state.Id), "project.json"), state);
    }
    public IEnumerable<object> Recent()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "project.json", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == Root)
            .Select(p => { try { return JsonFile.Read<ProjectState>(p); } catch { return null; } })
            .Where(p => p is not null).OrderByDescending(p => p!.UpdatedUtc)
        .Select(p => new { p!.Id, p.CardId, p.ChineseName, p.IsEffect, p.UpdatedUtc, Completed = p.Parts.Count(x => x.Status != "pending"), Total = p.Parts.Count });
    }
    internal (CardChoice Card, string Variant, string Version, string ConfigPath, string Source, string SkeletonFile, string AtlasFile) OpenModel(NewProjectRequest request)
    {
        var card = Choices().SingleOrDefault(c => c.Id == request.CardId)
            ?? throw new InvalidDataException("目录中没有这张卡的可编辑动画");
        var variant = string.IsNullOrEmpty(request.Variant) ? card.Variants.FirstOrDefault(x => x.Contains("高清")) ?? card.Variants[0] : request.Variant;
        if (!card.Variants.Contains(variant)) throw new InvalidDataException("所选地区/画质不存在");
        var version = OnDemandLibrary.Ensure(Workspace,card.VersionFolder,variant);
        var configPath = Under(version, variant);
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        if (config["atlases"]!.AsArray().Count != 1) throw new InvalidDataException("此版本先支持单 Atlas、多 PNG 页；该模型有多个 Atlas，请选择其他版本");
        var source = Path.GetDirectoryName(configPath)!;
        var skeletonFile = config["skeleton"]!.GetValue<string>();
        var atlasFile = config["atlases"]![0]!["atlas"]!.GetValue<string>();
        return (card, variant, version, configPath, source, skeletonFile, atlasFile);
    }
    public ProjectState Create(NewProjectRequest request)
    {
        var (card, variant, version, configPath, source, skeletonFile, atlasFile) = OpenModel(request);
        return CreateParts(card.Id, card.Name, variant, version, source, skeletonFile, atlasFile, Path.GetFileName(configPath), false);
    }

    internal ProjectState CreateEffect(EffectMap map, string source, string model)
    {
        var variant = model == "贴图一览.json" ? "卡片效果 · 整页贴图" :
            model.StartsWith("原Spine-highend_hd-") ? "卡片效果 · 原生 Spine 高清" :
            model.StartsWith("原Spine-sd-") ? "卡片效果 · 原生 Spine 低清" :
            model.StartsWith("原Spine-") ? "卡片效果 · 原生 Spine" : "卡片效果 · " + Path.GetFileNameWithoutExtension(model);
        return CreateParts(map.CardId, map.Name, variant, "", source, model, Path.ChangeExtension(model, ".atlas"), EffectPackage.MapName, true);
    }

    private ProjectState CreateParts(int cardId, string cardName, string variant, string version,
        string source, string skeletonFile, string atlasFile, string extraFile, bool isEffect)
    {
        var skeleton = JsonNode.Parse(File.ReadAllText(Under(source, skeletonFile)))!;
        var spine = skeleton["skeleton"]?["spine"]?.GetValue<string>() ?? "未知";
        if (!spine.StartsWith("4.2.")) throw new InvalidDataException($"当前版本验证范围为 Spine 4.2，所选为 {spine}");
        var atlas = Atlas.Parse(File.ReadAllText(Under(source, atlasFile)));
        var state = new ProjectState
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + cardId + "-" + Guid.NewGuid().ToString("N")[..6],
            CardId = cardId, ChineseName = cardName, SourceVersion = version, Variant = variant,
            SkeletonFile = skeletonFile, AtlasFile = atlasFile, SpineVersion = spine, IsEffect = isEffect
        };
        var root = DirectoryFor(state.Id);
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = Path.Combine(root, "source");
            Directory.CreateDirectory(snapshot);
            foreach (var name in atlas.Pages.Keys.Concat([skeletonFile, atlasFile, extraFile]).Distinct())
            {
                var input = Under(source, name);
                var destination = Under(snapshot, name);
                File.Copy(input, destination);
                state.SourceHashes[name] = Hash(destination);
            }
            var attachments = new List<(string Skin, string Slot, string Name, JsonObject Data)>();
            foreach (var skin in skeleton["skins"]!.AsArray())
                foreach (var slot in skin!["attachments"]!.AsObject())
                    foreach (var attachment in slot.Value!.AsObject())
                    {
                        var data = attachment.Value!.AsObject();
                        if ((data["type"]?.GetValue<string>() ?? "region") is "region" or "mesh" or "linkedmesh")
                            attachments.Add((skin["name"]!.GetValue<string>(), slot.Key, attachment.Key, data));
                    }
            string[] preferred = ["Head", "HeadHairA", "HeadHairB", "HeadParts", "Eye_0", "Eye_1", "Hair", "LeftUpperArm", "RightArm", "LeftArm"];
            var ordered = attachments.OrderBy(a => Array.IndexOf(preferred, a.Name) is var i && i >= 0 ? i : 999).ThenBy(a => a.Name, StringComparer.Ordinal);
            var regionKeys = new HashSet<string>();
            var textureSizes = new Dictionary<string, (int Width, int Height)>();
            foreach (var a in ordered)
            {
                var data = (JsonObject)a.Data.DeepClone();
                if (data["type"]?.GetValue<string>() == "linkedmesh")
                {
                    var parent = attachments.SingleOrDefault(x => x.Slot == a.Slot && x.Name == data["parent"]!.GetValue<string>() && x.Skin == (data["skin"]?.GetValue<string>() ?? a.Skin));
                    if (parent.Data is null || parent.Data["uvs"] is null) throw new InvalidDataException("链接网格无法解析：" + a.Name);
                    data["uvs"] = parent.Data["uvs"]!.DeepClone();
                    data["triangles"] = parent.Data["triangles"]!.DeepClone();
                }
                var part = atlas.MakePart(state.Parts.Count + 1, a.Slot, a.Name, data);
                if (!textureSizes.TryGetValue(part.Page, out var textureSize))
                    textureSizes[part.Page] = textureSize = RigbyExport.TextureSize(Under(snapshot, part.Page));
                atlas.FitToTexture(part, textureSize.Width, textureSize.Height);
                var key = part.Page + ":" + string.Join(',', part.Bounds) + ":" + string.Join(',', part.Offsets);
                if (!regionKeys.Add(key)) continue; // Shared image / linked meshes only require one replacement.
                state.Parts.Add(part);
                var folder = Path.Combine(root, "templates", part.Key);
                Directory.CreateDirectory(folder);
                using var page = Image.Load<Rgba32>(Under(snapshot, part.Page));
                using var raw = Atlas.Extract(page, part);
                raw.SaveAsPng(Path.Combine(folder, "raw.png"));
                using var roundtrip = Atlas.Pack(raw, part);
                var b = part.Bounds;
                for (var y = 0; y < roundtrip.Height; y++) for (var x = 0; x < roundtrip.Width; x++)
                    if (roundtrip[x, y] != page[x + b[0], y + b[1]]) throw new InvalidDataException("部件像素往返失败：" + part.Name);
                if (part.Pma) Atlas.ToStraight(raw);
                using var mesh = Atlas.MeshMask(part);
                using var maskRgba = Atlas.MaskRgba(mesh);
                maskRgba.SaveAsPng(Path.Combine(folder, "mesh.png"));
                for (var y = 0; y < raw.Height; y++) for (var x = 0; x < raw.Width; x++)
                {
                    var c = raw[x, y]; c.A = (byte)(c.A * mesh[x, y].PackedValue / 255); raw[x, y] = c;
                }
                raw.SaveAsPng(Path.Combine(folder, "reference.png"));
            }
            if (state.Parts.Count == 0) throw new InvalidDataException("没有可替换的图像部件");
            Save(state);
            return state;
        }
        catch
        {
            // Preserve any diagnostic files, but the absence of project.json keeps incomplete creations out of the project list.
            File.WriteAllText(Path.Combine(root, "创建未完成.txt"), "创建中断。未写入原素材和游戏。可在新项目中重试。");
            throw;
        }
    }
    public void SetArtwork(ProjectState p, Stream input)
    {
        if (p.Artwork.Length > 0) throw new InvalidOperationException("每个项目固定一张立绘；更换立绘请新建项目，以保留部件对应关系");
        using var art = LoadArtwork(input);
        p.Artwork = "artwork.png";
        JsonFile.Png(Under(DirectoryFor(p.Id), p.Artwork), art);
        Save(p);
    }
    private static Image<Rgba32> LoadArtwork(Stream input)
    {
        var info = Image.Identify(input);
        if ((long)info.Width * info.Height > 40_000_000)
            throw new InvalidDataException("图片过大，请缩小到 4000 万像素以内");
        input.Position = 0;
        return Image.Load<Rgba32>(input);
    }
    public string ArtworkPath(ProjectState p, Part? part)
    {
        var source = part is { DraftSource.Length: > 0 } ? part.DraftSource : p.Artwork;
        if (source.Length == 0) throw new InvalidOperationException("请先导入整张立绘或当前部件的图片");
        return Under(DirectoryFor(p.Id), source);
    }
    public Part SetPartArtwork(ProjectState p, Part part, Stream input, string? name)
    {
        using var art = LoadArtwork(input);
        var placement = WholeImagePlacement(p, part, art);
        var source = $"draft/{part.Key}/images/{Guid.NewGuid():N}.png";
        JsonFile.Png(Under(DirectoryFor(p.Id), source), art);
        part.DraftSource = source;
        part.DraftSourceName = Path.GetFileName(name) ?? "";
        return SaveWholeImage(p, part, placement);
    }
    public Part UseWholePartImage(ProjectState p, Part part)
    {
        if (part.DraftSource.Length == 0) throw new InvalidOperationException("请先导入当前部件的图片");
        using var art = Image.Load<Rgba32>(ArtworkPath(p, part));
        return SaveWholeImage(p, part, WholeImagePlacement(p, part, art));
    }
    private Part SaveWholeImage(ProjectState p, Part part, Placement placement)
    {
        part.DraftCutout = part.DraftSource;
        part.DraftMask = "";
        part.Placement = placement;
        Save(p);
        return part;
    }
    private Placement WholeImagePlacement(ProjectState p, Part part, Image<Rgba32> image)
    {
        // Use the main image for initial alignment, even if the export has faint alpha at the canvas edges.
        var visible = FindVisibleBounds(image, 32);
        if (visible.IsEmpty) visible = FindVisibleBounds(image, 8);
        if (visible.IsEmpty) throw new InvalidDataException("图片没有可用的可见像素，可使用「替换为空素材」");
        if (image.Width == part.Width && image.Height == part.Height)
            return new Placement { Width = image.Width, Height = image.Height, X = image.Width / 2d, Y = image.Height / 2d };
        return FitToReference(p, part, image.Size, visible);
    }
    public Part UseProjectArtwork(ProjectState p, Part part)
    {
        if (p.Artwork.Length == 0) throw new InvalidOperationException("请先导入整张立绘");
        ClearDraft(part);
        part.Placement = new Placement();
        Save(p);
        return part;
    }
    private static void ClearDraft(Part part)
    {
        part.DraftSource = part.DraftSourceName = part.DraftMask = part.DraftCutout = "";
    }
    public static Image<Rgba32> DecodePng(string base64)
    {
        var data = base64.StartsWith("data:") ? base64[(base64.IndexOf(',') + 1)..] : base64;
        if (data.Length > 100_000_000) throw new InvalidDataException("图片数据过大");
        return Image.Load<Rgba32>(Convert.FromBase64String(data));
    }
    public object SaveSelection(ProjectState p, Part part, string base64)
    {
        var root = DirectoryFor(p.Id);
        using var art = Image.Load<Rgba32>(ArtworkPath(p, part));
        using var mask = DecodePng(base64);
        if (art.Size != mask.Size) throw new InvalidDataException("选区尺寸与当前源图不一致");
        var left = art.Width; var top = art.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < art.Height; y++) for (var x = 0; x < art.Width; x++)
        {
            var c = art[x, y]; c.A = (byte)((c.A * mask[x, y].A + 127) / 255); art[x, y] = c.A == 0 ? default : c;
            if (c.A > 0) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        }
        if (right < left) throw new InvalidDataException("选区没有可见像素，请先圈选当前部件");
        var draft = Path.Combine(root, "draft", part.Key);
        Directory.CreateDirectory(draft);
        part.DraftMask = $"draft/{part.Key}/mask.png";
        part.DraftCutout = $"draft/{part.Key}/cutout.png";
        JsonFile.Png(Under(root, part.DraftMask), mask);
        art.Mutate(x => x.Crop(new Rectangle(left, top, right - left + 1, bottom - top + 1)));
        JsonFile.Png(Under(root, part.DraftCutout), art);
        part.Placement = FitToReference(p, part, art.Size, new Rectangle(0, 0, art.Width, art.Height));
        Save(p);
        return new { Part = part, Crop = new { Left = left, Top = top, Width = art.Width, Height = art.Height } };
    }
    private Placement FitToReference(ProjectState p, Part part, Size size, Rectangle visible)
    {
        using var reference = Image.Load<Rgba32>(Under(DirectoryFor(p.Id), $"templates/{part.Key}/reference.png"));
        var box = VisibleBounds(reference);
        var scale = Math.Min((double)box.Width / visible.Width, (double)box.Height / visible.Height);
        return new Placement { Width = size.Width * scale, Height = size.Height * scale,
            X = box.X + box.Width / 2d + (size.Width / 2d - visible.X - visible.Width / 2d) * scale,
            Y = box.Y + box.Height / 2d + (size.Height / 2d - visible.Y - visible.Height / 2d) * scale };
    }
    public static Rectangle VisibleBounds(Image<Rgba32> image)
    {
        var bounds = FindVisibleBounds(image, 8);
        return bounds.IsEmpty ? new Rectangle(0, 0, image.Width, image.Height) : bounds;
    }
    private static Rectangle FindVisibleBounds(Image<Rgba32> image, byte alphaThreshold)
    {
        int l = image.Width, t = image.Height, r = -1, b = -1;
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++) if (image[x, y].A > alphaThreshold)
            { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
        return r >= l ? new Rectangle(l, t, r - l + 1, b - t + 1) : Rectangle.Empty;
    }
    public void KeepPending(ProjectState p)
    {
        foreach(var part in p.Parts.Where(x=>x.Status=="pending").ToArray())Confirm(p,part,null);
    }
    public void ConfirmCutout(ProjectState p, Part part, Placement placement)
    {
        if (part.DraftCutout.Length == 0) throw new InvalidDataException("请先生成镂空裁片");
        using var cutout = Image.Load<Rgba32>(Under(DirectoryFor(p.Id), part.DraftCutout));
        using var aligned = CutoutRenderer.Render(cutout, placement, part.Width, part.Height);
        using var data = new MemoryStream(); aligned.SaveAsPng(data);
        Confirm(p, part, new ConfirmRequest(Convert.ToBase64String(data.ToArray()), placement));
    }
    public void Confirm(ProjectState p, Part part, ConfirmRequest? request, bool empty = false)
    {
        var root = DirectoryFor(p.Id);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..5];
        var rel = $"revisions/{part.Key}/{id}";
        var folder = Under(root, rel);
        Directory.CreateDirectory(folder);
        var revision = new Revision { Kind = empty ? "empty" : request is null ? "original" : "replaced", Placement = request?.Placement ?? part.Placement };
        if (empty)
        {
            using var blank = new Image<Rgba32>(part.Width, part.Height);
            blank.SaveAsPng(Path.Combine(folder, "aligned.png"));
            revision.Aligned = rel + "/aligned.png";
            ClearDraft(part);
        }
        else if (request is not null)
        {
            if (part.DraftCutout.Length == 0) throw new InvalidDataException("请先圈选、生成对齐预览");
            using var aligned = DecodePng(request.Image);
            if (aligned.Width != part.Width || aligned.Height != part.Height) throw new InvalidDataException("对齐画布尺寸错误");
            using var mesh = Image.Load<Rgba32>(Path.Combine(root, "templates", part.Key, "mesh.png"));
            long visible = 0;
            for (var y = 0; y < aligned.Height; y++) for (var x = 0; x < aligned.Width; x++)
                if (mesh[x, y].A != 0 && aligned[x, y].A > 8) visible++;
            if (visible == 0) throw new InvalidDataException("新部件没有落在原网格内，请调整位置和尺寸");
            aligned.SaveAsPng(Path.Combine(folder, "aligned.png"));
            if (part.DraftMask.Length != 0)
            {
                File.Copy(Under(root, part.DraftMask), Path.Combine(folder, "mask.png"));
                revision.Mask = rel + "/mask.png";
            }
            File.Copy(Under(root, part.DraftCutout), Path.Combine(folder, "cutout.png"));
            revision.Source = part.DraftSource; revision.SourceName = part.DraftSourceName;
            revision.Cutout = rel + "/cutout.png"; revision.Aligned = rel + "/aligned.png";
            part.Placement = request.Placement;
        }
        JsonFile.Write(Path.Combine(folder, "revision.json"), revision);
        part.ConfirmedRevision = rel + "/revision.json";
        part.Revisions.Add(part.ConfirmedRevision);
        part.Status = revision.Kind;
        p.CurrentPart = Math.Min(p.Parts.Count - 1, part.Number);
        Save(p);
    }
    public void Restore(ProjectState p, Part part, string revisionPath)
    {
        if (!part.Revisions.Contains(revisionPath)) throw new InvalidDataException("版本不属于当前部件");
        var root = DirectoryFor(p.Id);
        var revision = JsonFile.Read<Revision>(Under(root, revisionPath));
        part.Status = revision.Kind; part.ConfirmedRevision = revisionPath; part.Placement = revision.Placement;
        ClearDraft(part);
        if (revision.Kind == "replaced")
        {
            var draft = Under(root, "draft/" + part.Key); Directory.CreateDirectory(draft);
            part.DraftSource = revision.Source; part.DraftSourceName = revision.SourceName;
            if (revision.Mask.Length != 0)
            {
                part.DraftMask = $"draft/{part.Key}/mask.png";
                File.Copy(Under(root, revision.Mask), Under(root, part.DraftMask), true);
            }
            part.DraftCutout = $"draft/{part.Key}/cutout.png";
            File.Copy(Under(root, revision.Cutout), Under(root, part.DraftCutout), true);
        }
        Save(p);
    }
    public static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    public static string HashBytes(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
