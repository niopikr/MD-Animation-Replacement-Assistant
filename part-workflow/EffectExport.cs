using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CardAssetWorkbench;

namespace PartWorkflow;

internal sealed record EffectExportRequest(int CardId);

internal static class EffectExport
{
    private static readonly SemaphoreSlim Extraction = new(1);

    internal static async Task<(FileStream Content, string FileName)> Create(
        string workspace, GameSettings settings, int cardId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.GameRoot) || string.IsNullOrWhiteSpace(settings.Account))
            throw new InvalidOperationException("请先在首页应用游戏目录和账号。");
        var card = EffectCatalog.Read(workspace).SingleOrDefault(effect => effect.CardId == cardId)
            ?? throw new InvalidDataException("效果目录中没有这张卡，请更新目录后重新选择。");
        var index = Path.Combine(workspace, "数据", "索引", "index.json");
        if (!File.Exists(index))
            throw new FileNotFoundException("素材索引不存在，请先更新动画素材与目录。");
        var runtime = AppEnvironment.Resource("effect-runtime");
        var interpreter = Path.Combine(runtime, "python.exe");
        var script = Path.Combine(runtime, "extract_effects.py");
        if (!File.Exists(interpreter) || !File.Exists(script))
            throw new FileNotFoundException("效果提取组件缺失，请保留新版完整程序文件夹。");

        await Extraction.WaitAsync(cancellationToken);
        var target = Path.Combine(AppEnvironment.Runtime(workspace), $"effect-source-{Guid.NewGuid():N}.zip");
        var delivered = false;
        try
        {
            var start = new ProcessStartInfo(interpreter)
            {
                WorkingDirectory = runtime,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] {
                "-B", "-X", "utf8", script, "--index", index,
                "--game-root", settings.GameRoot, "--account", settings.Account,
                "--only", cardId.ToString(CultureInfo.InvariantCulture), "--zip", target,
            }) start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地效果提取。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync();
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("提取效果素材超时，请重试。");
            }
            await stdout;
            var error = (await stderr).Trim();
            if (process.ExitCode != 0 || !File.Exists(target))
            {
                if (process.ExitCode == 2 && error.Length is > 0 and <= 250)
                    throw new InvalidDataException(error);
                if (error.Length != 0)
                    File.AppendAllText(Path.Combine(AppEnvironment.Runtime(workspace), "效果提取.log"), error + Environment.NewLine);
                throw new InvalidDataException("无法提取此卡的效果素材，请确认已下载游戏资源并刷新目录。详情见效果提取日志。");
            }

            var zip = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.DeleteOnClose);
            try
            {
                Validate(zip, cardId);
                zip.Position = 0;
                delivered = true;
                return (zip, BulkLibrary.SafeComponent($"{cardId}-{card.Name}-效果素材") + ".zip");
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        finally
        {
            Extraction.Release();
            File.Delete(target + ".tmp");
            if (!delivered) File.Delete(target);
        }
    }

    private static void Validate(Stream stream, int cardId)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        if (!entries.TryGetValue(EffectPackage.MapName, out var manifest))
            throw new InvalidDataException("提取的效果素材缺少回包映射。");
        using (var mapping = manifest.Open())
        {
            var map = JsonSerializer.Deserialize<EffectMap>(mapping);
            if (map is null || map.CardId != cardId || map.Format != EffectPackage.MapFormat ||
                map.Pages is not { Length: > 0 } || map.Pages.Any(p => !entries.ContainsKey(p.File)))
                throw new InvalidDataException("效果贴图与回包映射不匹配。");
        }

        var atlases = entries.Values.Where(e => e.Name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (atlases.Length == 0) throw new InvalidDataException("素材包缺少 Rigby2D 图集。");
        foreach (var atlas in atlases)
        {
            using var reader = new StreamReader(atlas.Open(), Encoding.UTF8);
            var pages = ReadAtlasPages(reader);
            if (!entries.ContainsKey(Path.ChangeExtension(atlas.Name, ".json")) ||
                pages.Count == 0 || pages.Any(name => !entries.ContainsKey(name)))
                throw new InvalidDataException("Rigby2D 骨骼 JSON 或贴图页缺失：" + atlas.Name);
        }
    }

    private static List<string> ReadAtlasPages(TextReader reader)
    {
        var pages = new List<string>();
        var expectPage = true;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                expectPage = true;
                continue;
            }
            if (!expectPage || line.Contains(':')) continue;
            pages.Add(line);
            expectPage = false;
        }
        return pages;
    }
}
