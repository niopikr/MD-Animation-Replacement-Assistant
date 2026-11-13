using System.IO.Compression;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

internal sealed record EffectDependency(string Bundle, string BundleSha256);
internal sealed record EffectTarget(string Bundle, string BundleSha256, int FileIndex, long PathId, string Name,
    EffectDependency[]? Via = null);
internal sealed record EffectPage(string File, string OriginalPngSha256, int Width, int Height, int Format, int Mips, EffectTarget[] Targets);
internal sealed record EffectMap(string Format, int CardId, string Name, EffectPage[] Pages);
internal sealed record EffectFolderRequest(string Folder, int? CardId = null);

internal static class EffectPackage
{
    internal const string MapName = "效果贴图映射.json";
    internal const string MapFormat = "md-unity-effect-textures-v1";
    private static readonly Regex BundlePath = new(@"^0000/([0-9a-f]{2})/([0-9a-f]{8})$", RegexOptions.IgnoreCase);
    private static readonly Regex Sha = new(@"^[0-9a-f]{64}$", RegexOptions.IgnoreCase);

    internal static (FileStream Content, string FileName) Pack(string workspace, GameSettings settings, string directory, int? cardId = null)
    {
        if (string.IsNullOrWhiteSpace(settings.GameRoot) || string.IsNullOrWhiteSpace(settings.Account))
            throw new InvalidOperationException("请先在首页应用游戏目录和账号。");
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("请先选择效果素材文件夹。");
        var folder = Path.GetFullPath(directory.Trim().Trim('"'));
        var mapPath = Path.Combine(folder, MapName);
        if (!File.Exists(mapPath) || new FileInfo(mapPath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("请选择包含「效果贴图映射.json」的已解压效果素材文件夹。");
        var map = JsonFile.Read<EffectMap>(mapPath);
        if (map.Format != MapFormat || map.CardId <= 0 || string.IsNullOrWhiteSpace(map.Name) || map.Pages is not { Length: > 0 and <= 250 })
            throw new InvalidDataException("效果素材清单格式不匹配。");
        if (cardId is not null && cardId != map.CardId)
            throw new InvalidDataException($"该文件夹属于「{map.Name}」〔{map.CardId}〕，请在效果目录选择同一张卡。");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in map.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.File) || Path.GetFileName(page.File) != page.File ||
                page.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !page.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !names.Add(page.File) ||
                page.Width is < 1 or > 8192 || page.Height is < 1 or > 8192 || page.Mips is < 1 or > 14 ||
                !Sha.IsMatch(page.OriginalPngSha256) || page.Targets is not { Length: > 0 and <= 64 })
                throw new InvalidDataException("效果素材清单含无效贴图记录。");
        }
        var cache = AppEnvironment.Runtime(workspace);
        var work = Path.Combine(cache, $"effect-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var prepared = EffectAtlasEdits.Prepare(folder, work, map.Pages);
            return PackPrepared(work, settings, folder, map, prepared);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static (FileStream Content, string FileName) PackPrepared(string work, GameSettings settings, string folder, EffectMap map, Dictionary<string, string> prepared)
    {
        var edits = new Dictionary<string, List<(EffectPage Page, EffectTarget Target, string Png)>>(StringComparer.OrdinalIgnoreCase);
        var dependencyChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in map.Pages)
        {
            var png = prepared.GetValueOrDefault(page.File) ?? ProjectStore.Under(folder, page.File);
            if (!File.Exists(png)) continue;
            if (ProjectStore.Hash(png).Equals(page.OriginalPngSha256, StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(png).Length > 128 * 1024 * 1024)
                throw new InvalidDataException("PNG 文件过大：" + page.File);
            foreach (var target in page.Targets)
            {
                var path = BundlePath.Match(target.Bundle);
                if (!path.Success || !path.Groups[2].Value.StartsWith(path.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ||
                    !Sha.IsMatch(target.BundleSha256) || target.FileIndex is < 0 or > 64 || string.IsNullOrWhiteSpace(target.Name) ||
                    target.Via is { Length: > 8 })
                    throw new InvalidDataException("效果素材清单含无效 Bundle 定位记录。");
                if (target.Via is { Length: > 0 } via &&
                    dependencyChecks.Add(string.Join('|', via.Select(x => x.Bundle + ":" + x.BundleSha256).Append(target.Bundle))))
                    CheckDependencies(settings, map.CardId, via, target.Bundle);
                if (!edits.TryGetValue(target.Bundle, out var group)) edits.Add(target.Bundle, group = []);
                group.Add((page, target, png));
            }
        }
        if (edits.Count == 0) throw new InvalidDataException("没有找到改动的 PNG。请保留导出时的文件名和尺寸。");

        var resultPath = work + ".zip";
        var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.DeleteOnClose);
        var encodedPages = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (bundle, group) in edits)
                {
                    var source = ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}/{bundle}");
                    if (!File.Exists(source)) throw new FileNotFoundException("当前账号没有对应的原始效果 Bundle：" + bundle);
                    var expected = group[0].Target.BundleSha256;
                    if (group.Any(x => !x.Target.BundleSha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) ||
                        group.Select(x => (x.Target.FileIndex, x.Target.PathId)).Distinct().Count() != group.Count)
                        throw new InvalidDataException("效果清单存在重复或冲突的 Bundle 记录。");
                    var backup = Path.Combine(work, Path.GetFileName(bundle) + ".original");
                    File.Copy(source, backup);
                    if (!ProjectStore.Hash(backup).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("游戏 Bundle 已变化或已安装其他修改，请先恢复原包或重新提取：" + bundle);
                    var replacement = Path.Combine(work, Path.GetFileName(bundle) + ".replacement");
                    foreach (var entry in group)
                        if (!encodedPages.ContainsKey(entry.Png))
                            encodedPages[entry.Png] = Encode(entry.Page, entry.Png);
                    Rewrite(backup, replacement, map.CardId, group.Select(x => (x.Page, x.Target, Bytes: encodedPages[x.Png])).ToArray());
                    if (!ProjectStore.Hash(source).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("打包期间游戏效果资源发生变化，请重试：" + bundle);
                    zip.CreateEntryFromFile(backup, "原始/" + bundle, CompressionLevel.NoCompression);
                    zip.CreateEntryFromFile(replacement, "替换/" + bundle, CompressionLevel.NoCompression);
                }
            }
            stream.Position = 0;
            return (stream, BulkLibrary.SafeComponent($"{map.CardId}-{map.Name}-效果替换") + ".zip");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void CheckDependencies(GameSettings settings, int cardId, EffectDependency[] via, string target)
    {
        if (via.Select(x => x.Bundle).Append(target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != via.Length + 1)
            throw new InvalidDataException("效果依赖记录存在循环。");
        var manager = AssetIndex.Manager();
        try
        {
            for (var i = 0; i < via.Length; i++)
            {
                var entry = via[i];
                var match = BundlePath.Match(entry.Bundle);
                if (!match.Success || !match.Groups[2].Value.StartsWith(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ||
                    !Sha.IsMatch(entry.BundleSha256))
                    throw new InvalidDataException("效果依赖记录无效。");
                var file = ProjectStore.Under(settings.GameRoot, $"LocalData/{settings.Account}/{entry.Bundle}");
                if (!File.Exists(file) || !ProjectStore.Hash(file).Equals(entry.BundleSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("效果依赖文件已变化，请重新提取：" + entry.Bundle);
                var next = i + 1 < via.Length ? via[i + 1].Bundle : target;
                var nextId = Path.GetFileName(next);
                var bundle = manager.LoadBundleFile(file, true);
                var belongsToCard = i > 0;
                var linksToTarget = false;
                for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
                {
                    if (!bundle.file.IsAssetsFile(index)) continue;
                    var cab = manager.LoadAssetsFileFromBundle(bundle, index, false);
                    manager.LoadClassDatabaseFromPackage(cab.file.Metadata.UnityVersion);
                    foreach (var info in cab.file.GetAssetsOfType(AssetClassID.AssetBundle))
                    {
                        var descriptor = manager.GetBaseField(cab, info);
                        belongsToCard |= HasEffectContainer(descriptor, cardId);
                        linksToTarget |= descriptor["m_Dependencies"]["Array"].Children.Any(dependency =>
                            Path.GetFileName(dependency.AsString.Replace('\\', '/')).Equals(nextId, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!belongsToCard || !linksToTarget)
                    throw new InvalidDataException("效果贴图的游戏依赖链与清单不符：" + entry.Bundle);
                manager.UnloadAll(false);
            }
        }
        finally { manager.UnloadAll(); }
    }

    private static bool HasEffectContainer(AssetTypeValueField descriptor, int cardId)
    {
        var effectPath = new Regex($@"/duel/timeline/card/(?:.*?/)?ef0*{cardId}/", RegexOptions.IgnoreCase);
        return descriptor["m_Container"]["Array"].Children.Any(entry => effectPath.IsMatch(entry["first"].AsString));
    }

    private static byte[] Encode(EffectPage page, string png)
    {
        var info = Image.Identify(png);
        if (info.Width != page.Width || info.Height != page.Height)
            throw new InvalidDataException($"「{page.File}」须保持 {page.Width} × {page.Height}。");
        using var image = Image.Load<Rgba32>(png);
        if (image.Frames.Count != 1) throw new InvalidDataException("请使用单帧 PNG：" + page.File);
        image.Mutate(x => x.Flip(FlipMode.Vertical));
        var rgba = new byte[checked(page.Width * page.Height * 4)];
        image.CopyPixelDataTo(rgba);
        var format = page.Format switch
        {
            4 => CompressionFormat.Rgba,
            10 => CompressionFormat.Bc1WithAlpha,
            12 => CompressionFormat.Bc3,
            25 => CompressionFormat.Bc7,
            27 => CompressionFormat.Bc5,
            _ => throw new InvalidDataException($"当前回包不支持贴图格式 {page.Format}：{page.File}")
        };
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.GenerateMipMaps = page.Mips > 1;
        encoder.OutputOptions.MaxMipMapLevel = page.Mips;
        var levels = encoder.EncodeToRawBytes(rgba, page.Width, page.Height, BCnEncoder.Encoder.PixelFormat.Rgba32);
        if (levels.Length != page.Mips) throw new InvalidDataException("贴图 mipmap 层数不符：" + page.File);
        return levels.SelectMany(x => x).ToArray();
    }

    private static void Rewrite(string original, string destination, int cardId, (EffectPage Page, EffectTarget Target, byte[] Bytes)[] edits)
    {
        var raw = destination + ".raw";
        var am = AssetIndex.Manager();
        try
        {
            var bundle = am.LoadBundleFile(original, true);
            var dirs = bundle.file.BlockAndDirInfo.DirectoryInfos;
            var names = dirs.Select(d => d.Name).ToArray();
            var expected = Enumerable.Range(0, names.Length).Select(i => ReadEntry(bundle, i)).ToArray();
            var changed = new HashSet<int>();
            var verified = new HashSet<int>();
            foreach (var (page, target, bytes) in edits)
            {
                if (target.FileIndex >= names.Length || !bundle.file.IsAssetsFile(target.FileIndex))
                    throw new InvalidDataException("原 Bundle 中缺少指定的 CAB。");
                var af = am.LoadAssetsFileFromBundle(bundle, target.FileIndex, false);
                am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                if (target.Via is not { Length: > 0 } && verified.Add(target.FileIndex) &&
                    !af.file.GetAssetsOfType(AssetClassID.AssetBundle).Any(a => HasEffectContainer(am.GetBaseField(af, a), cardId)))
                    throw new InvalidDataException("原 Bundle 不属于清单中的效果动画：" + target.Bundle);
                var info = af.file.GetAssetInfo(target.PathId) ?? throw new InvalidDataException("原 Bundle 中找不到贴图：" + target.Name);
                if (info.TypeId != (int)AssetClassID.Texture2D)
                    throw new InvalidDataException("资源对象类型与贴图清单不符。");
                var field = am.GetBaseField(af, info);
                var tex = TextureFile.ReadTextureFile(field);
                if (tex.m_Name != target.Name || tex.m_Width != page.Width || tex.m_Height != page.Height ||
                    tex.m_TextureFormat != page.Format || tex.m_MipCount != page.Mips)
                    throw new InvalidDataException("原贴图的名称、尺寸、格式或 mipmap 与清单不符：" + target.Name);
                tex.FillPictureData(af);
                if (tex.pictureData.Length != bytes.Length)
                    throw new InvalidDataException("压缩结果长度与原贴图不符：" + target.Name);
                var streamData = field["m_StreamData"];
                var streamPath = streamData.IsDummy ? "" : streamData["path"].AsString;
                int entry;
                int offset;
                if (!string.IsNullOrEmpty(streamPath))
                {
                    var resourceName = Path.GetFileName(streamPath.Replace('\\', '/'));
                    entry = Array.FindIndex(names, n => Path.GetFileName(n.Replace('\\', '/')) == resourceName);
                    offset = checked((int)streamData["offset"].AsULong);
                    if (entry < 0 || streamData["size"].AsUInt != bytes.Length)
                        throw new InvalidDataException("找不到原贴图流或流长度不符：" + target.Name);
                }
                else
                {
                    entry = target.FileIndex;
                    var start = checked((int)info.GetAbsoluteByteOffset(af.file));
                    var span = expected[entry].AsSpan(start, checked((int)info.ByteSize));
                    var inside = span.IndexOf(tex.pictureData);
                    if (inside < 0 || span[(inside + 1)..].IndexOf(tex.pictureData) >= 0)
                        throw new InvalidDataException("无法唯一定位 CAB 内嵌贴图：" + target.Name);
                    offset = start + inside;
                }
                if (!expected[entry].AsSpan(offset, bytes.Length).SequenceEqual(tex.pictureData))
                    throw new InvalidDataException("原贴图字节与定位不符：" + target.Name);
                bytes.CopyTo(expected[entry], offset);
                changed.Add(entry);
            }
            foreach (var entry in changed) dirs[entry].SetNewData(expected[entry]);
            using (var writer = new AssetsFileWriter(raw)) bundle.file.Write(writer);
            am.UnloadAll(false);
            var unpacked = am.LoadBundleFile(raw);
            using (var writer = new AssetsFileWriter(destination)) unpacked.file.Pack(writer, AssetBundleCompressionType.LZ4);
            am.UnloadAll(false);
            var verify = am.LoadBundleFile(destination, true);
            if (!verify.file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name).SequenceEqual(names) ||
                Enumerable.Range(0, names.Length).Any(i => !ReadEntry(verify, i).AsSpan().SequenceEqual(expected[i])))
                throw new InvalidDataException("效果 Bundle 回读校验失败。");
        }
        finally
        {
            am.UnloadAll();
            File.Delete(raw);
        }
    }

    private static byte[] ReadEntry(BundleFileInstance bundle, int index)
    {
        bundle.file.GetFileRange(index, out var offset, out var size);
        bundle.file.DataReader.Position = offset;
        return bundle.file.DataReader.ReadBytes(checked((int)size));
    }
}
