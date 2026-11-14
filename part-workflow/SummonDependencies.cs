using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PartWorkflow;

internal sealed record SummonTarget(GameReplacementExporter.Locator Skeleton, GameReplacementExporter.Locator Atlas,
    GameReplacementExporter.Locator[] Pages, string SkeletonFile, string AtlasFile, int[][] Dimensions,
    string[] Qualities, string[] Animations);

internal sealed record SummonDependencies(SummonTarget[] Models, Dictionary<string, string> Hashes, string[]? Bindings = null)
{
    internal static string Label(string variant) => Path.GetFileName(Path.GetDirectoryName(variant))!;

    private static Match VariantMatch(string variant) => Regex.Match(Label(variant), @"^(.*?)-(高清|低清)(?:-|$)");

    internal static string Region(string variant) => VariantMatch(variant).Groups[1].Value.Replace('-', '/');

    internal static string Quality(string variant) =>
        VariantMatch(variant).Groups[2].Value switch { "高清" => "highend_hd", "低清" => "sd", _ => "" };

    internal static IEnumerable<string> Companions(CardChoice card, string variant) =>
        card.Variants.Where(candidate => candidate != variant && Quality(candidate).Length != 0 &&
            Region(candidate) == Region(variant));

    internal static SummonDependencies Read(string workspace, GameSettings settings, int card, string variant, string work,
        bool shareBindings = false)
    {
        var index = Path.Combine(workspace, "数据", "索引", "index.json");
        var region = Region(variant);
        if (region.Length == 0 || !File.Exists(index)) return new([], []);
        var runtime = AppEnvironment.Resource("effect-runtime");
        var start = new ProcessStartInfo(Path.Combine(runtime, "python.exe"))
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var value in new[] { "-B", "-X", "utf8", Path.Combine(runtime, "summon_dependencies.py"),
            index, settings.GameRoot, settings.Account, card.ToString(System.Globalization.CultureInfo.InvariantCulture), region, work })
            start.ArgumentList.Add(value);
        if (shareBindings) start.ArgumentList.Add("share-bindings");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动召唤依赖检查。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.WaitForExit();
            throw new TimeoutException("读取召唤引用超时。");
        }
        if (process.ExitCode == 2)
            throw new InvalidDataException(stdout.GetAwaiter().GetResult().Trim());
        if (process.ExitCode != 0)
        {
            File.AppendAllText(Path.Combine(AppEnvironment.Runtime(workspace), "召唤打包.log"),
                stderr.GetAwaiter().GetResult() + Environment.NewLine);
            throw new InvalidDataException("无法读取召唤资源引用，详情见召唤打包日志。");
        }
        return JsonFile.Read<SummonDependencies>(Path.Combine(work, "dependencies.json"));
    }
}
