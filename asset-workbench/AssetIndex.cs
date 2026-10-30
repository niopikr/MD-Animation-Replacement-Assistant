using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Microsoft.Data.Sqlite;
using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CardAssetWorkbench;

public record AssetRecord(int FileIndex, long PathId, string Type, string Name, int Width, int Height);
public record BundleRecord(string Path, string Source, long Length, long ModifiedTicks, List<string> Containers, List<AssetRecord> Assets);
public record CardEntry(int Id, string Name, List<string> Names);
public record FailedBundleRecord(string Path,long Length,long ModifiedTicks,string Error);
public class GameIndex
{
    public int Version { get; set; } = 3;
    public string GameRoot { get; set; } = "";
    public string Account { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<BundleRecord> Bundles { get; set; } = [];
    public List<CardEntry> Cards { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public Dictionary<int,string> ChineseNames { get; set; } = [];
    public List<string> ChineseNameSources { get; set; } = [];
    public int CandidateFiles { get; set; }
    public int ReusedBundles { get; set; }
    public List<FailedBundleRecord> FailedFiles { get; set; }=[];
    public int ReusedFailures { get; set; }
    public double ScanSeconds { get; set; }
}

public static class AssetIndex
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string ClassData => File.Exists(Path.Combine(AppContext.BaseDirectory,"资源","classdata.tpk"))?Path.Combine(AppContext.BaseDirectory,"资源","classdata.tpk"):Path.Combine(AppContext.BaseDirectory,"classdata.tpk");
    static readonly object ClassGate = new();
    public static AssetsManager Manager()
    {
        var am = new AssetsManager();
        lock (ClassGate) am.LoadClassPackage(ClassData);
        return am;
    }
    public static byte[] Script(AssetTypeValueField field)
    {
        try { return field["m_Script"].AsByteArray; }
        catch { return field["m_Script"]["Array"].AsByteArray; }
    }
    public static void Save<T>(string path, T obj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(obj, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    public static GameIndex Load(string path) => JsonSerializer.Deserialize<GameIndex>(File.ReadAllText(path))!;
    public static List<string> Accounts(string game) => Directory.GetDirectories(Path.Combine(game, "LocalData"))
        .Where(d => Directory.Exists(Path.Combine(d,"0000"))).Order().Select(Path.GetFileName).Select(x=>x!).ToList();

    public static GameIndex Scan(string game, string account, string cache, string database, Action<string> log, CancellationToken ct)
    {
        game = Path.GetFullPath(game);
        var scanWatch=System.Diagnostics.Stopwatch.StartNew();
        if (!File.Exists(Path.Combine(game, "masterduel.exe"))) throw new IOException("目录中没有 masterduel.exe。");
        if (!Accounts(game).Contains(account)) throw new IOException("请选择当前安装中的账号目录。");
        var roots = new[] { (Path.Combine(game,"LocalData",account,"0000"), "LocalData"),
            (Path.Combine(game,"masterduel_Data","StreamingAssets","AssetBundle"), "StreamingAssets") };
        var files = roots.Where(r=>Directory.Exists(r.Item1)).SelectMany(r=>Directory.EnumerateFiles(r.Item1,"*",SearchOption.AllDirectories).Select(p=>(Path:p,Source:r.Item2))).ToList();
        // Other installed accounts are fallback sources only; never replace the selected account's bundle.
        var existingNames=files.Select(f=>Path.GetFileName(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var other in Accounts(game).Where(a=>a!=account))
        foreach(var path in Directory.EnumerateFiles(Path.Combine(game,"LocalData",other,"0000"),"*",SearchOption.AllDirectories))
            if(existingNames.Add(Path.GetFileName(path)))files.Add((path,"AccountFallback:"+other));
        var indexPath = Path.Combine(cache,"index.json");
        var old = File.Exists(indexPath) ? Load(indexPath) : new GameIndex();
        var oldMap = old.Bundles.ToDictionary(b=>b.Path, StringComparer.OrdinalIgnoreCase);
        var oldFailures=old.FailedFiles.ToDictionary(b=>b.Path,StringComparer.OrdinalIgnoreCase);
        var failedFiles=new ConcurrentBag<FailedBundleRecord>();
        var bundles = new ConcurrentBag<BundleRecord>(); var errors = new ConcurrentBag<string>();
        var payloads = new ConcurrentBag<(string Name,string Group,byte[] Bytes)>();
        int count=0,reused=0,reusedFailures=0;
        log($"正在检查 {files.Count:N0} 个文件；游戏文件只读。");
        Parallel.ForEach(files, new ParallelOptions{MaxDegreeOfParallelism=3,CancellationToken=ct},
            Manager, (item, loop, am)=>
        {
            ct.ThrowIfCancellationRequested();
            var n=Interlocked.Increment(ref count); if(n%1000==0) log($"资源检查 {n:N0}/{files.Count:N0}，已收录 {bundles.Count:N0} 个 Bundle。");
            var fi = new FileInfo(item.Path);
            if(oldFailures.TryGetValue(item.Path,out var failure)&&failure.Length==fi.Length&&failure.ModifiedTicks==fi.LastWriteTimeUtc.Ticks)
            {failedFiles.Add(failure);errors.Add(failure.Error);Interlocked.Increment(ref reusedFailures);return am;}
            // Name bundles are reopened so multilingual names can be rebuilt from current data.
            if (oldMap.TryGetValue(item.Path,out var prior) && prior.Length==fi.Length && prior.ModifiedTicks==fi.LastWriteTimeUtc.Ticks
                && !prior.Assets.Any(a=>a.Name.StartsWith("CARD_",StringComparison.OrdinalIgnoreCase))) {bundles.Add(prior with{Source=item.Source});Interlocked.Increment(ref reused);}
            else try
            {
                using (var s=File.OpenRead(item.Path)) { Span<byte> magic=stackalloc byte[7]; if(s.Read(magic)!=7 || !magic.SequenceEqual("UnityFS"u8)) return am; }
                var b=am.LoadBundleFile(item.Path,true);
                var containers=new List<string>(); var assets=new List<AssetRecord>();
                for(int f=0;f<b.file.BlockAndDirInfo.DirectoryInfos.Count;f++)
                {
                    if(!b.file.IsAssetsFile(f)) continue;
                    var af=am.LoadAssetsFileFromBundle(b,f,false);
                    lock(ClassGate) am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                    foreach(var info in af.file.GetAssetsOfType(AssetClassID.AssetBundle))
                    {
                        var field=am.GetBaseField(af,info);
                        foreach(var entry in field["m_Container"]["Array"]) containers.Add(entry["first"].AsString);
                    }
                    foreach(var type in new[]{AssetClassID.Texture2D,AssetClassID.TextAsset,AssetClassID.AudioClip})
                    foreach(var info in af.file.GetAssetsOfType(type))
                    {
                        var field=am.GetBaseField(af,info); var name=field["m_Name"].AsString;
                        assets.Add(new(f,info.PathId,type.ToString(),name,type==AssetClassID.Texture2D?field["m_Width"].AsInt:0,type==AssetClassID.Texture2D?field["m_Height"].AsInt:0));
                        if(type==AssetClassID.TextAsset && Regex.IsMatch(name,@"^CARD_(Name|Indx|Prop)(\.bytes)?$",RegexOptions.IgnoreCase))
                        {
                            var c=containers.FirstOrDefault(x=>x.Contains(name,StringComparison.OrdinalIgnoreCase))??"unknown/";
                            payloads.Add((name.ToLowerInvariant().Replace(".bytes",""),c[..(c.LastIndexOf('/')+1)],Script(field)));
                        }
                    }
                }
                bundles.Add(new(item.Path,item.Source,fi.Length,fi.LastWriteTimeUtc.Ticks,containers,assets));
            }
            catch(Exception ex){var error=item.Path+" | "+ex.GetType().Name+": "+ex.Message;errors.Add(error);failedFiles.Add(new(item.Path,fi.Length,fi.LastWriteTimeUtc.Ticks,error));}
            finally
            {
                am.UnloadAll(false);
            }
            return am;
        }, am=>am.UnloadAll());
        var names=new Dictionary<int,HashSet<string>>();
        var chineseNames=new Dictionary<int,string>();var chineseSources=new List<string>();
        if(File.Exists(database))
        {
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly}.ToString()); db.Open();
            using var q=db.CreateCommand(); q.CommandText="SELECT id,name FROM card";
            using var reader=q.ExecuteReader(); while(reader.Read()) Add(reader.GetInt32(0),reader.GetString(1));
        }
        byte[] Decode(byte[] bytes)
        {
            try { return CardDataCrypto.Decrypt(bytes,CardDataCrypto.FindCryptoKey(bytes,minPlainLength:8)); }
            catch { return bytes; } // Some locally supplied name tables are already plaintext.
        }
        var decoded=payloads.Select(p=>(p.Name,p.Group,Bytes:Decode(p.Bytes))).ToList();
        var prop=decoded.FirstOrDefault(p=>p.Name=="card_prop").Bytes;
        foreach(var group in decoded.GroupBy(p=>p.Group))
        {
            var n=group.FirstOrDefault(p=>p.Name=="card_name").Bytes; var ix=group.FirstOrDefault(p=>p.Name=="card_indx").Bytes;
            var pr=group.FirstOrDefault(p=>p.Name=="card_prop").Bytes??prop;
            if(n is null || ix is null || pr is null) continue;
            var ids=CardDataFilesParser.ParseCardIds(pr);var ns=CardDataFilesParser.SplitIndexedStrings(ix,n,0);
            var isChinese=group.Key.Contains("/zh-cn/",StringComparison.OrdinalIgnoreCase);
            if(isChinese)chineseSources.Add(group.Key);
            for(int i=0;i<Math.Min(ids.Count,ns.Count);i++)
            {
                Add(ids[i],ns[i]);
                if(isChinese&&!string.IsNullOrWhiteSpace(ns[i]))chineseNames[ids[i]]=ns[i];
            }
        }
        // The requested card's familiar Chinese names are aliases, not a claim about the installed language.
        Add(16386,"鲜花女男爵"); Add(16386,"鮮花女男爵"); Add(16386,"Baronne de Fleur");
        var result=new GameIndex{GameRoot=game,Account=account,Bundles=bundles.OrderBy(x=>x.Path).ToList(),Errors=errors.Order().ToList(),
            Cards=names.OrderBy(p=>p.Key).Select(p=>new CardEntry(p.Key,p.Value.First(),p.Value.ToList())).ToList(),
            ChineseNames=chineseNames,ChineseNameSources=chineseSources,CandidateFiles=files.Count,ReusedBundles=reused,
            FailedFiles=failedFiles.OrderBy(f=>f.Path).ToList(),ReusedFailures=reusedFailures,ScanSeconds=scanWatch.Elapsed.TotalSeconds};
        Save(indexPath,result); WriteCatalog(result,cache);
        log($"完成：{result.Bundles.Count:N0} 个 Bundle（复用 {reused:N0}），{result.ChineseNames.Count:N0} 个游戏中文名，{result.Errors.Count} 个读取错误。");
        return result;
        void Add(int id,string name){if(string.IsNullOrWhiteSpace(name)||id<=0)return;if(!names.TryGetValue(id,out var set))names[id]=set=[];set.Add(name);}
    }

    public static List<BundleRecord> Related(GameIndex index,int id) => index.Bundles.Where(b=>
        b.Assets.Any(a=>Regex.IsMatch(a.Name,$@"(?i)^(?:illust_|p|card_|cardicon_){id}(?!\d)")) ||
        b.Containers.Any(c=>Regex.IsMatch(c,$@"(?i)(?:[/_]|^)(?:p|illust_)?{id}(?!\d)")))
        .GroupBy(b=>Path.GetFileName(b.Path),StringComparer.OrdinalIgnoreCase)
        .Select(g=>g.OrderBy(b=>b.Source=="LocalData"?0:b.Source.StartsWith("AccountFallback:")?1:2).First()).ToList();
    public static bool IsAnimation(BundleRecord b) => b.Containers.Any(c=>Regex.IsMatch(c,@"(?i)monster.*(?:cutin|cut-in)|(?:spine|summon|highend)|/p\d+"))
        || b.Assets.Any(a=>Regex.IsMatch(a.Name,@"(?i)^p\d+(?:[._-]|$)"));
    // Only the pNNNNN directory is a card identity. P163862.png, for example, is
    // page 2 of P16386, not a separate card numbered 163862.
    public static IEnumerable<int> AnimationCardIds(BundleRecord b) => b.Containers
        .Where(c=>c.Contains("/monstercutin/",StringComparison.OrdinalIgnoreCase))
        .SelectMany(s=>Regex.Matches(s,@"(?i)/p(\d+)(?:/|$)").Cast<Match>())
        .Select(m=>int.Parse(m.Groups[1].Value)).Distinct();
    public static List<int> AnimationIds(GameIndex index) => index.Bundles.SelectMany(AnimationCardIds).Distinct().Order().ToList();
    static string Csv(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
    public static void WriteCatalog(GameIndex index,string cache)
    {
        var groups=index.Bundles.SelectMany(b=>AnimationCardIds(b).Select(id=>(Id:id,Bundle:b)))
            .GroupBy(p=>p.Id).OrderBy(g=>g.Key);
        var map=index.Cards.ToDictionary(c=>c.Id);
        var rows=new List<string>{"卡号,卡名及别名,检测到的Bundle数,资源证据,说明"};
        foreach(var group in groups)
        {
            var id=group.Key;var bs=group.Select(p=>p.Bundle).ToList();
            rows.Add($"{id},{Csv(map.TryGetValue(id,out var c)?string.Join(" / ",c.Names):"未匹配卡名")},{bs.Count},{Csv(string.Join(" | ",bs.SelectMany(b=>b.Containers)))},{Csv("本机资源证据；不等于游戏内播放验收或完整可替换")}");
        }
        File.WriteAllLines(Path.Combine(cache,"召唤动画卡名目录.csv"),rows,new UTF8Encoding(true));
        File.WriteAllLines(Path.Combine(cache,"扫描错误.txt"),index.Errors,new UTF8Encoding(true));
    }
    public static string Safe(string name) => string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
    public static string Sha(string file){using var s=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}

    public static string Export(GameIndex index,int id,string destination,Action<string> log)
    {
        var related=Related(index,id); if(related.Count==0) throw new IOException("没有定位到此卡号的专属资源。请确认游戏内已下载素材。");
        var name=index.Cards.FirstOrDefault(c=>c.Id==id)?.Name??id.ToString();
        var output=Path.Combine(destination,$"{id}-{Safe(name)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        GuardOutsideGame(index,output); Directory.CreateDirectory(output);
        var manifests=new List<object>();
        foreach(var b in related)
        {
            var fi=new FileInfo(b.Path);
            if(fi.Length!=b.Length || fi.LastWriteTimeUtc.Ticks!=b.ModifiedTicks) throw new IOException("游戏资源已变化，请重新扫描后导出："+b.Path);
            var dir=Path.Combine(output,"bundles",Path.GetFileName(b.Path)); Directory.CreateDirectory(dir);
            File.Copy(b.Path,Path.Combine(dir,"original.bundle"));
            var am=Manager();
            var exports=new List<object>();
            try
            {
                var bundle=am.LoadBundleFile(b.Path,true);
                foreach(var group in b.Assets.GroupBy(a=>a.FileIndex))
                {
                    var af=am.LoadAssetsFileFromBundle(bundle,group.Key,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                    foreach(var a in group)
                    {
                        var info=af.file.GetAssetInfo(a.PathId);var field=am.GetBaseField(af,info);
                        var file=$"{a.FileIndex}-{a.PathId}-{Safe(a.Name)}";
                        if(a.Type=="Texture2D")
                        {
                            var t=TextureFile.ReadTextureFile(field);t.FillPictureData(af);
                            var pixels=t.DecodeTextureRaw(t.pictureData)??throw new IOException("无法解码纹理："+a.Name);
                            // TextureDecoder returns BGRA in Unity's bottom-up texture orientation.
                            // Export a standard top-down PNG without resizing, trimming, or alpha conversion.
                            using var image=Image.LoadPixelData<Bgra32>(pixels,t.m_Width,t.m_Height);
                            image.Mutate(ctx=>ctx.Flip(FlipMode.Vertical));
                            file+=".png";image.SaveAsPng(Path.Combine(dir,file));
                        }
                        else if(a.Type=="TextAsset") {file+=".txt";File.WriteAllBytes(Path.Combine(dir,file),Script(field));}
                        else {file+=".assetbin";af.file.Reader.Position=info.GetAbsoluteByteOffset(af.file);File.WriteAllBytes(Path.Combine(dir,file),af.file.Reader.ReadBytes(checked((int)info.ByteSize)));}
                        exports.Add(new{Asset=a,File=Path.GetRelativePath(output,Path.Combine(dir,file)),Sha256=Sha(Path.Combine(dir,file))});
                    }
                }
                // Preserve every bundle entry as well, including resS, shaders and serialized objects.
                var raw=Path.Combine(dir,"raw");Directory.CreateDirectory(raw);
                for(int i=0;i<bundle.file.BlockAndDirInfo.DirectoryInfos.Count;i++)
                {
                    var entry=bundle.file.BlockAndDirInfo.DirectoryInfos[i];
                    bundle.file.GetFileRange(i,out var offset,out var size);bundle.file.DataReader.Position=offset;
                    File.WriteAllBytes(Path.Combine(raw,$"{i}-{Safe(entry.Name)}"),bundle.file.DataReader.ReadBytes(checked((int)size)));
                }
            }
            finally{am.UnloadAll();}
            manifests.Add(new{Bundle=b,Sha256=Sha(b.Path),Exports=exports});log("已导出 "+Path.GetFileName(b.Path));
        }
        Save(Path.Combine(output,"manifest.json"),new{CardId=id,CardName=name,Game=index.GameRoot,Account=index.Account,CreatedUtc=DateTime.UtcNow,
            Scope="卡号/资源名/容器路径直接关联的本机素材；公共特效、独立音效和间接共享依赖不保证穷尽。原 Bundle 与全部内部条目一并保留。",
            TextureExport="TextureDecoder BGRA → 标准 PNG，垂直翻转；保持原尺寸、像素与 alpha，不拉伸、不预乘/反预乘。",Bundles=manifests});
        return output;
    }
    public static void GuardOutsideGame(GameIndex index,string path)
    {
        var root=Path.GetFullPath(index.GameRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var full=Path.GetFullPath(path);
        if(full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||full.TrimEnd('\\')==root.TrimEnd('\\'))throw new IOException("输出不能放在游戏安装目录内。");
    }
}
