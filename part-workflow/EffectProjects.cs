using System.IO.Compression;
using System.Text.Json.Nodes;

namespace PartWorkflow;

internal sealed record EffectProjectRequest(int CardId, string? Model = null);

internal static class EffectProjects
{
    internal static async Task<object> Open(ProjectStore store, GameSettings settings, EffectProjectRequest request,
        CancellationToken cancellationToken)
    {
        var work = Path.Combine(AppEnvironment.Runtime(store.Workspace), $"effect-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            using var archive = (await EffectExport.Create(store.Workspace, settings, request.CardId, cancellationToken)).Content;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name != entry.FullName) throw new InvalidDataException("效果素材含无效文件路径。");
                entry.ExtractToFile(ProjectStore.Under(work, entry.Name));
            }
            var (_, map) = EffectPackage.ReadFolder(work, request.CardId);
            var models = Directory.GetFiles(work, "*.atlas").Select(file => Path.ChangeExtension(Path.GetFileName(file), ".json"))
                .Where(model => model != "贴图一览.json")
                .Select(model =>
                {
                    var document = JsonNode.Parse(File.ReadAllText(ProjectStore.Under(work, model)))!;
                    var variant = map.NativeModels?.SingleOrDefault(native => native.Model == model);
                    var label = variant is null ? Path.GetFileNameWithoutExtension(model) :
                        model.StartsWith("原Spine-highend_hd-") ? "原生 Spine 高清" :
                        model.StartsWith("原Spine-sd-") ? "原生 Spine 低清" : Path.GetFileNameWithoutExtension(model);
                    return new { Model = model, Label = label, Native = variant is not null,
                        Animated = document["animations"] is JsonObject { Count: > 0 },
                        HasParts = document["slots"] is JsonArray { Count: > 0 } };
                }).Where(model => model.HasParts).OrderByDescending(model => model.Native)
                .ThenByDescending(model => model.Animated).ThenBy(model => model.Model, StringComparer.Ordinal).ToArray();
            if (models.Length == 0)
                models = [new { Model = "贴图一览.json", Label = "整页贴图", Native = false, Animated = false, HasParts = true }];
            if (request.Model is null && models.Length > 1) return new { Models = models };
            var selected = request.Model ?? models[0].Model;
            if (!models.Any(model => model.Model == selected)) throw new InvalidDataException("所选效果模型已变化，请重新选择。");
            lock (store.Gate) return new { Project = store.CreateEffect(map, work, selected) };
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    internal static object Export(ProjectStore store, Exporter exporter, ProjectState project)
    {
        var snapshot = (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(exporter.Export(project, true, gamePackage: true))!;
        var folder = snapshot["Folder"]!.GetValue<string>();
        var model = snapshot["ModelFolder"]!.GetValue<string>();
        var pages = Atlas.Parse(File.ReadAllText(ProjectStore.Under(model, project.AtlasFile))).Pages.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = new InstallSettings(store.Workspace).Read();
        using var stream = EffectPackage.Pack(store.Workspace, settings, model, project.CardId, projectPages: pages).Content;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            const string prefix = "替换/";
            if (!entry.FullName.StartsWith(prefix + "0000/", StringComparison.Ordinal))
                throw new InvalidDataException("效果替换包目录无效。");
            var destination = ProjectStore.Under(Path.Combine(folder, "替换文件"), entry.FullName[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
        File.WriteAllText(Path.Combine(folder, "跨画质导出完成标记.txt"), "已验证效果替换文件。");
        return new { Folder = folder, Relative = snapshot["Relative"]!.GetValue<string>(), Changes = snapshot["Changes"]!.GetValue<int>(),
            BundleCount = zip.Entries.Count, CompanionCount = 0 };
    }
}
