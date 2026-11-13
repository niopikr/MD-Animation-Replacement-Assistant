using System.Text.RegularExpressions;
using CardAssetWorkbench;

namespace PartWorkflow;

internal sealed record EffectBundleInfo(string Bundle, string LogicalPath);
internal sealed record EffectCatalogEntry(int CardId, string Name, EffectBundleInfo[] Prefabs, EffectBundleInfo[] Timelines, EffectBundleInfo[] Other);

internal static class EffectCatalog
{
    internal static string PathFor(string workspace) => Path.Combine(workspace, "数据", "效果目录.json");
    internal static EffectCatalogEntry[] Read(string workspace)
    {
        var path = PathFor(workspace);
        return File.Exists(path) ? JsonFile.Read<EffectCatalogEntry[]>(path) : [];
    }

    internal static void Update(GameIndex index, string workspace)
    {
        var pattern = new Regex(@"/duel/timeline/card/(?:.*?/)?ef(\d+)/(.*)$", RegexOptions.IgnoreCase);
        var hits = index.Bundles.SelectMany(b => b.Containers.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (b, path, match: pattern.Match(path))))
            .Where(x => x.match.Success && int.TryParse(x.match.Groups[1].Value, out _))
            .Select(x => (id: int.Parse(x.match.Groups[1].Value), resource: new EffectBundleInfo(Path.GetFileName(x.b.Path), x.path), tail: x.match.Groups[2].Value))
            .GroupBy(x => x.id)
            .OrderBy(g => g.Key);
        var effects = new List<EffectCatalogEntry>();
        foreach (var group in hits)
        {
            var prefabs = group.Where(x => x.tail.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(x.tail), $@"^ef0*{group.Key}(?!\d)", RegexOptions.IgnoreCase)).Select(x => x.resource).Distinct().ToArray();
            var timelines = group.Where(x => x.tail.EndsWith(".playable", StringComparison.OrdinalIgnoreCase)).Select(x => x.resource).Distinct().ToArray();
            if (prefabs.Length == 0 && timelines.Length == 0) continue;
            var other = group.Select(x => x.resource).Except(prefabs).Except(timelines).ToArray();
            effects.Add(new(group.Key, index.ChineseNames.GetValueOrDefault(group.Key) ?? $"卡号 {group.Key}", prefabs, timelines, other));
        }
        JsonFile.Write(PathFor(workspace), effects);
    }
}
