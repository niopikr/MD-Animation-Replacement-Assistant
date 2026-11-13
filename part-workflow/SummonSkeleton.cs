using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PartWorkflow;

internal static class SummonSkeleton
{
    private sealed record Constraint(string Kind, string Name, JsonObject Data, int Order);

    internal static (byte[] Bytes, JsonObject Model, List<string> Repairs) Prepare(byte[] edited, byte[] original)
    {
        var model = Read(edited);
        var baseline = Read(original);
        var version = Version.Parse(model["skeleton"]!["spine"]!.GetValue<string>());
        var expected = Version.Parse(baseline["skeleton"]!["spine"]!.GetValue<string>());
        if (version.Major != expected.Major || version.Minor != expected.Minor)
            throw new InvalidDataException($"骨架版本是 {version}，所选游戏资源需要 Spine {expected.Major}.{expected.Minor}。");
        var repairs = new List<string>();
        if (version.Major != 4 || version.Minor != 2 || edited.AsSpan().SequenceEqual(original))
            return (edited, model, repairs);

        var constraints = ReadConstraints(model);
        var originalOrders = ReadConstraints(baseline).ToDictionary(c => (c.Kind, c.Name), c => c.Order);
        var used = constraints.Where(c => c.Order >= 0 && c.Order < constraints.Length)
            .Select(c => c.Order).ToHashSet();
        foreach (var group in constraints.GroupBy(c => c.Order))
        {
            var valid = group.Key >= 0 && group.Key < constraints.Length;
            if (valid && group.Count() == 1) continue;
            var owners = valid ? group.Where(c => originalOrders.GetValueOrDefault((c.Kind, c.Name), -1) == group.Key).ToArray() : [];
            if (valid && owners.Length != 1) throw OrderError(group.Key, group, constraints.Length);
            foreach (var constraint in group.Where(c => !owners.Contains(c)))
            {
                if (!originalOrders.TryGetValue((constraint.Kind, constraint.Name), out var order) ||
                    order < 0 || order >= constraints.Length || !used.Add(order))
                    throw OrderError(group.Key, group, constraints.Length);
                constraint.Data["order"] = order;
                repairs.Add($"{constraint.Name} {constraint.Order} → {order}");
            }
        }
        return (repairs.Count == 0 ? edited : JsonSerializer.SerializeToUtf8Bytes(model), model, repairs);
    }

    private static JsonObject Read(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\ufeff')) is JsonObject model &&
                model["skeleton"]?["spine"] is JsonValue version && version.TryGetValue<string>(out var value) &&
                Version.TryParse(value, out _) && model["bones"] is JsonArray { Count: > 0 } &&
                model["slots"] is JsonArray && model["skins"] is JsonArray or JsonObject)
                return model;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("召唤骨架 JSON 无法解析。请选择 Spine JSON + Atlas 导出文件。", ex);
        }
        throw new InvalidDataException("召唤骨架 JSON 缺少 Spine 版本、骨骼、插槽或皮肤数据。");
    }

    private static Constraint[] ReadConstraints(JsonObject model) =>
        new[] { "ik", "transform", "path", "physics" }
            .SelectMany(kind => (model[kind]?.AsArray() ?? []).Select(entry =>
            {
                var data = entry!.AsObject();
                return new Constraint(kind, data["name"]!.GetValue<string>(), data, data["order"]?.GetValue<int>() ?? 0);
            })).ToArray();

    private static InvalidDataException OrderError(int order, IEnumerable<Constraint> group, int count) =>
        new($"约束顺序 {order} 无法按游戏原版自动恢复（{string.Join("、", group.Select(c => c.Name))}）。" +
            $"请在导出 JSON 中为每条约束使用 0 至 {count - 1} 之间互不重复的编号。");

    internal static void CheckRegions(JsonObject model, Atlas atlas)
    {
        var skins = model["skins"] is JsonArray list
            ? list.OfType<JsonObject>().Select(skin => skin["attachments"]).OfType<JsonObject>()
            : model["skins"]!.AsObject().Select(skin => skin.Value).OfType<JsonObject>();
        foreach (var skin in skins)
            foreach (var slot in skin.Select(item => item.Value).OfType<JsonObject>())
                foreach (var (name, item) in slot)
                {
                    var type = item?["type"]?.GetValue<string>() ?? "region";
                    if (type is not ("region" or "mesh" or "linkedmesh") || item?["sequence"] is not null) continue;
                    var region = item?["path"]?.GetValue<string>() ?? name;
                    if (!atlas.Regions.ContainsKey(region))
                        throw new InvalidDataException($"图集缺少骨架引用的部件「{region}」。请使用同一次导出的 JSON 和 atlas。");
                }
    }
}
