using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

public sealed class Exporter(ProjectStore store)
{
    public object Export(ProjectState project, bool final, bool gamePackage = false)
    {
        if (final && project.Parts.Any(p => p.Status == "pending")) throw new InvalidOperationException("仍有未确认部件。逐项替换或明确选择保留原部件后，才能导出最终素材。");
        var root = store.DirectoryFor(project.Id);
        var source = Path.Combine(root, "source");
        foreach (var item in project.SourceHashes)
            if (ProjectStore.Hash(ProjectStore.Under(source, item.Key)) != item.Value) throw new InvalidDataException("项目原素材快照被改变：" + item.Key);
        var kind = gamePackage ? "内部导出" : final ? "最终动画素材" : "整体预览";
        var run = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..5]}";
        var relative = gamePackage ? $"{kind}/{run}/{project.CardId}-{SafeName(project.ChineseName)}" : $"{kind}/{run}";
        var destination = ProjectStore.Under(root, relative);
        var model = Path.Combine(destination, "动画模型");
        Directory.CreateDirectory(model);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(model, Path.GetFileName(file)));
        var atlas = Atlas.Parse(File.ReadAllText(Path.Combine(source, project.AtlasFile)));
        var changes = new List<object>();
        var replacements = project.Parts.Where(p => p.Status is "replaced" or "empty").ToArray();
        var skeleton = JsonNode.Parse(File.ReadAllText(Path.Combine(source, project.SkeletonFile)))!;
        foreach (var part in replacements)
        {
            var regionName = part.RegionName;
            if (string.IsNullOrEmpty(regionName))
            {
                foreach (var skin in skeleton["skins"]!.AsArray())
                {
                    var data = skin?["attachments"]?[part.Slot]?[part.Name];
                    if (data is not null) { regionName = data["path"]?.GetValue<string>() ?? part.Name; break; }
                }
            }
            if (string.IsNullOrEmpty(regionName) || !atlas.Regions.TryGetValue(regionName, out var originalRegion))
                throw new InvalidDataException("无法定位原图集部件：" + part.Name);
            var oldPageFields = atlas.Pages[originalRegion.Page];
            var revision = JsonFile.Read<Revision>(ProjectStore.Under(root, part.ConfirmedRevision));
            using var image = Image.Load<Rgba32>(ProjectStore.Under(root, revision.Aligned));
            if (image.Width != part.Width || image.Height != part.Height) throw new InvalidDataException("确认部件尺寸错误：" + part.Name);
            if (part.Pma) Atlas.ToPma(image);
            var name = $"_replacement_{part.Number:D3}.png";
            var path = Path.Combine(model, name);
            if (File.Exists(path)) throw new InvalidDataException("源素材占用了替换文件名：" + name);
            image.SaveAsPng(path);
            using var reload = Image.Load<Rgba32>(path);
            for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
                if (image[x,y] != reload[x,y]) throw new InvalidDataException("替换贴图像素验证失败：" + part.Name);
            // Each replacement owns a complete transparent canvas. Old polygon-packed pixels cannot leak in.
            var originalWidth = part.AtlasOffsets.Length == 4 ? part.AtlasOffsets[2] : part.Width;
            var originalHeight = part.AtlasOffsets.Length == 4 ? part.AtlasOffsets[3] : part.Height;
            var pageFields = new Dictionary<string,string> {
                ["size"] = $"{originalWidth},{originalHeight}", ["filter"] = "Linear,Linear", ["pma"] = part.Pma ? "true" : "false"
            };
            if (oldPageFields.TryGetValue("scale", out var scale)) pageFields["scale"] = scale;
            atlas.Pages.Add(name, pageFields);
            atlas.Regions[regionName] = new Atlas.Region { Name = regionName, Page = name, Fields = new Dictionary<string,string> {
                ["bounds"] = $"0,0,{originalWidth},{originalHeight}", ["offsets"] = $"0,0,{originalWidth},{originalHeight}", ["rotate"] = "0"
            }};
            var components = Path.Combine(destination,"已确认部件"); Directory.CreateDirectory(components);
            File.Copy(ProjectStore.Under(root,revision.Aligned),Path.Combine(components,part.Key+"-"+SafeName(part.Name)+".png"));
            changes.Add(new {part.Number,part.Name,SourcePage=part.Page,ExportPage=name,part.Status,part.ConfirmedRevision});
        }
        if (replacements.Length > 0)
        {
            var text = new StringBuilder();
            foreach (var page in atlas.Pages.ToArray())
            {
                var regions = atlas.Regions.Values.Where(r => r.Page == page.Key).ToArray();
                if (regions.Length == 0) { atlas.Pages.Remove(page.Key); continue; }
                text.AppendLine(page.Key);
                foreach (var field in page.Value) text.AppendLine($"{field.Key}: {field.Value}");
                foreach (var region in regions)
                {
                    text.AppendLine(region.Name);
                    foreach (var field in region.Fields) text.AppendLine($"  {field.Key}: {field.Value}");
                }
                text.AppendLine();
            }
            File.WriteAllText(Path.Combine(model,project.AtlasFile),text.ToString(),new UTF8Encoding(false));
            foreach (var configFile in Directory.GetFiles(model,"*.config.json"))
            {
                var config=JsonNode.Parse(File.ReadAllText(configFile))!;
                config["atlases"]![0]!["tex_names"]=new JsonArray(atlas.Pages.Keys.Select(n=>(JsonNode?)JsonValue.Create(Path.GetFileNameWithoutExtension(n))).ToArray());
                config["atlases"]![0]!["textures"]=new JsonArray(atlas.Pages.Keys.Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray());
                File.WriteAllText(configFile,config.ToJsonString(JsonFile.Options));
            }
        }
        var report = new
        {
            FormatVersion = 1, Kind = kind, project.CardId, project.ChineseName, project.SpineVersion, project.Variant,
            project.SourceVersion, ProjectId = project.Id, CreatedUtc = DateTime.UtcNow,
            Parts = project.Parts.Select(p => new { p.Number, p.Name, p.Label, p.Status, p.ConfirmedRevision,
                Placement = p.ConfirmedRevision.Length == 0 ? null : JsonFile.Read<Revision>(ProjectStore.Under(root, p.ConfirmedRevision)).Placement }),
            Changes = changes,
            Files = Directory.GetFiles(model).ToDictionary(f => Path.GetFileName(f)!, ProjectStore.Hash),
            OriginalSourceHashes = project.SourceHashes,
            GameInstallationModified = false,
            PackageType = "Spine 原位置替换素材（不是 Unity AssetBundle 安装补丁）"
        };
        JsonFile.Write(Path.Combine(destination, "导出记录.json"), report);
        JsonFile.Write(Path.Combine(destination, "部件映射.json"), project.Parts);
        File.WriteAllText(Path.Combine(destination, "先看这里.txt"),
            $"{project.ChineseName}〔{project.CardId}〕 — {kind}\r\n\r\n" +
            "动画模型 文件夹含原骨骼 JSON、Atlas、修改后的全部 PNG 页和 EX Studio 配置。\r\n" +
            "EX Studio → Live2D/Spine 编辑器 → 选择 Json 配置文件 → 选择 动画模型/预览.config.json。\r\n" +
            "随后在动画页选择 animation 并勾选循环。Spine SDK 使用 4.2。\r\n" +
            "已确认部件 是标准透明 PNG；动画模型 中的 PNG 则已按原图集规则处理 PMA。\r\n" +
            "此包是可预览、可继续制作的 Spine 素材包，不是可直接覆盖游戏的 AssetBundle 补丁。\r\n" +
            (final ? "全部部件已明确确认。" : "这是阶段预览，尚未确认的部件沿用原素材。") +
            "\r\n可返回工作流项目重做任意部件，再生成新的预览或导出；已有导出不覆盖。\r\n", new UTF8Encoding(true));
        if (!AppEnvironment.Portable(store.Workspace)) File.WriteAllText(Path.Combine(destination, "在EX Studio中预览.cmd"),
            "@echo off\r\nchcp 65001 >nul\r\nset \"model=%~dp0动画模型\\预览.config.json\"\r\n" +
            "powershell -NoProfile -Command \"Set-Clipboard -Value $env:model\"\r\n" +
            "start \"\" explorer.exe \"%~dp0动画模型\"\r\n" +
            "echo 请在 EX Studio 的 选择Json配置文件 中选择上方文件夹的预览.config.json。\r\n" +
            "pause\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(destination, gamePackage ? "辅助预览完成标记.txt" : "导出完成标记.txt"), "素材及像素保存验证已完成。\r\n");
        if (AppEnvironment.Portable(store.Workspace)) File.WriteAllText(Path.Combine(destination,"先看这里.txt"), "使用程序内的整体动画预览查看此快照。可返回项目重做任意部件，再生成新的预览或替换文件。\r\n", new UTF8Encoding(true));
        project.Exports.Add(relative); store.Save(project);
        return new { Folder = destination, Relative = relative, ModelFolder = model, Config = Directory.GetFiles(model,"*.config.json").FirstOrDefault() ?? "", IsFinal = final, Changes = changes.Count };
    }
    static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
