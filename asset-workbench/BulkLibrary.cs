using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CardAssetWorkbench;

public record SourceDigest(string BundleId,string Path,string Sha256,long Length,long ModifiedTicks);
public record OutputDigest(string File,string Sha256,long Length,long ModifiedTicks);
public record LibraryAsset(string BundleId,AssetRecord Asset,string LogicalPath,string File,string Sha256);
public class CardState
{
    public int Id {get;set;}
    public string ChineseName {get;set;}="";
    public string Folder {get;set;}="";
    public string VersionFolder {get;set;}="";
    public string Fingerprint {get;set;}="";
    public string Status {get;set;}="";
    public int BundleCount {get;set;}
    public int TextureCount {get;set;}
    public int PreviewCount {get;set;}
    public List<OutputDigest> Files {get;set;}=[];
}
public class LibraryState
{
    public string Format {get;set;}="md-card-library-v1";
    public string GameRoot {get;set;}="";
    public DateTime UpdatedUtc {get;set;}
    public Dictionary<int,CardState> Cards {get;set;}=[];
}
public record HashCacheEntry(long Length,long ModifiedTicks,string Sha256);
public class BulkReport
{
    public string RunId {get;set;}="";
    public int Detected {get;set;}
    public int Added {get;set;}
    public int Updated {get;set;}
    public int Skipped {get;set;}
    public int Failed {get;set;}
    public int HistoricalOnly {get;set;}
    public int TexturesWritten {get;set;}
    public int OriginalFilesWritten {get;set;}
    public int PreviewGroupsWritten {get;set;}
    public double Seconds {get;set;}
    public List<string> Errors {get;set;}=[];
}

public static class BulkLibrary
{
    public const string ExporterVersion="bulk-1.0";
    static readonly Regex PathIds=new(@"(?:^|[/_])(?:p|illust_)?(\d+)(?!\d)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    static readonly Regex AssetIds=new(@"^(?:illust_|p|card_|cardicon_)(\d+)(?!\d)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    static readonly PngEncoder Encoder=new(){CompressionLevel=PngCompressionLevel.Level1};
    public static string SafeComponent(string text)
    {
        var name=AssetIndex.Safe(text.Normalize(NormalizationForm.FormC)).TrimEnd(' ','.');
        if(name.Length==0)name="未命名";
        if(Regex.IsMatch(name,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase))name="_"+name;
        if(name.Length>70)name=name[..54]+"_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..10];
        return name;
    }
    // Invert the association once. A texture-page suffix never supplies a shortened card ID.
    public static Dictionary<int,List<BundleRecord>> BuildAssociations(GameIndex index,IEnumerable<int> requested)
    {
        var wanted=requested.ToHashSet();var result=wanted.ToDictionary(i=>i,_=>new List<BundleRecord>());
        foreach(var b in index.Bundles)
        {
            var ids=b.Containers.SelectMany(c=>PathIds.Matches(c).Cast<Match>()).Concat(b.Assets.SelectMany(a=>AssetIds.Matches(a.Name).Cast<Match>()))
                .Select(m=>int.Parse(m.Groups[1].Value)).Where(wanted.Contains).Distinct();
            foreach(var id in ids)result[id].Add(b);
        }
        foreach(var id in wanted)result[id]=result[id].GroupBy(b=>Path.GetFileName(b.Path),StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.OrderBy(b=>b.Source=="LocalData"?0:b.Source.StartsWith("AccountFallback:")?1:2).First()).OrderBy(b=>Path.GetFileName(b.Path)).ToList();
        return result;
    }
    public static string Fingerprint(int id,string chineseName,IEnumerable<SourceDigest> sources)
    {
        var s=ExporterVersion+"\n"+id+"\n"+chineseName+"\n"+string.Join("\n",sources.OrderBy(s=>s.BundleId).Select(s=>s.BundleId+":"+s.Sha256));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
    static bool Intact(string library,CardState card,bool deep)
    {
        if(card.Files.Count==0)return false;
        foreach(var f in card.Files)
        {
            var path=Inside(library,f.File);var fi=new FileInfo(path);
            if(!fi.Exists||fi.Length!=f.Length||fi.LastWriteTimeUtc.Ticks!=f.ModifiedTicks)return false;
            if(deep&&AssetIndex.Sha(path)!=f.Sha256)return false;
        }
        return true;
    }
    public static string Inside(string root,string relative)
    {
        var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var full=Path.GetFullPath(Path.Combine(root,relative));
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("记录中的文件路径超出素材库："+relative);
        return full;
    }
    public static BulkReport Run(GameIndex index,string library,Action<string> log,CancellationToken ct,int[]? only=null,bool deep=false)
    {
        library=Path.GetFullPath(library);AssetIndex.GuardOutsideGame(index,library);Directory.CreateDirectory(library);
        using var runLock=new FileStream(Path.Combine(library,".运行锁"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var watch=Stopwatch.StartNew();var runId=DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var runDir=Inside(library,Path.Combine("运行记录",runId));Directory.CreateDirectory(runDir);
        var statePath=Path.Combine(library,"library-state.json");
        var state=File.Exists(statePath)?JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(statePath))!:new LibraryState();
        if(state.Format!="md-card-library-v1")throw new IOException("素材库版本不兼容，请使用新目录。");
        if(File.Exists(statePath))File.Copy(statePath,Path.Combine(runDir,"更新前状态.json"));
        state.GameRoot=index.GameRoot;
        var ids=AssetIndex.AnimationIds(index);if(only is not null)ids=ids.Where(only.Contains).ToList();
        if(ids.Count==0)throw new IOException("没有检测到可提取的召唤演出目录。");
        var missing=ids.Where(id=>!index.ChineseNames.ContainsKey(id)).ToList();
        if(missing.Count>0)throw new IOException("以下卡号缺少游戏简体中文名称，未用猜测名称保存："+string.Join(",",missing));
        var associations=BuildAssociations(index,ids);
        var report=new BulkReport{RunId=runId,Detected=ids.Count,HistoricalOnly=state.Cards.Keys.Except(AssetIndex.AnimationIds(index)).Count()};
        var hashesPath=Path.Combine(library,"source-hashes.json");
        var hashCache=File.Exists(hashesPath)?JsonSerializer.Deserialize<Dictionary<string,HashCacheEntry>>(File.ReadAllText(hashesPath))!:[];
        var allSources=new Dictionary<string,SourceDigest>(StringComparer.OrdinalIgnoreCase);
        var sourceErrors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var bundle in associations.Values.SelectMany(x=>x).DistinctBy(b=>b.Path))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi=new FileInfo(bundle.Path);
                if(!fi.Exists||fi.Length!=bundle.Length||fi.LastWriteTimeUtc.Ticks!=bundle.ModifiedTicks)throw new IOException("文件与扫描索引不一致，请重新刷新索引。");
                string hash;
                if(!deep&&hashCache.TryGetValue(bundle.Path,out var cached)&&cached.Length==fi.Length&&cached.ModifiedTicks==fi.LastWriteTimeUtc.Ticks)hash=cached.Sha256;
                else hash=AssetIndex.Sha(bundle.Path);
                hashCache[bundle.Path]=new(fi.Length,fi.LastWriteTimeUtc.Ticks,hash);
                allSources[bundle.Path]=new(Path.GetFileName(bundle.Path),bundle.Path,hash,fi.Length,fi.LastWriteTimeUtc.Ticks);
            }
            catch(Exception e){sourceErrors[bundle.Path]=e.Message;}
        }
        AssetIndex.Save(hashesPath,hashCache);
        AssetIndex.Save(Path.Combine(runDir,"输入摘要.json"),new{index.CreatedUtc,index.GameRoot,index.Account,index.CandidateFiles,index.ReusedBundles,
            index.ChineseNameSources,Detected=ids.Count,SourceBundles=allSources.Count,ScanErrors=index.Errors,SourceErrors=sourceErrors,DeepVerification=deep,ExporterVersion});
        log($"本轮 {ids.Count} 张卡，关联 {allSources.Count:N0} 个源文件；按游戏中文名保存。");
        var stateGate=new object();int processed=0;
        Parallel.ForEach(ids,new ParallelOptions{MaxDegreeOfParallelism=2,CancellationToken=ct},AssetIndex.Manager,(id,loop,am)=>
            {
                ct.ThrowIfCancellationRequested();
                var name=index.ChineseNames[id];CardState? previous;lock(stateGate)previous=state.Cards.GetValueOrDefault(id);
                try
                {
                    var bundles=associations[id];
                    if(bundles.Any(b=>sourceErrors.ContainsKey(b.Path)))throw new IOException(string.Join(" | ",bundles.Where(b=>sourceErrors.ContainsKey(b.Path)).Select(b=>b.Path+": "+sourceErrors[b.Path])));
                    var sources=bundles.Select(b=>allSources[b.Path]).ToList();
                    var fingerprint=Fingerprint(id,name,sources);
                    if(previous is not null&&previous.Fingerprint==fingerprint&&Intact(library,previous,deep))
                    {
                        lock(stateGate){report.Skipped++;processed++;if(processed%50==0)log($"进度 {processed}/{ids.Count}，未变化跳过 {report.Skipped} 张。");}
                        return am;
                    }
                    var folder=Path.Combine("卡片",SafeComponent(name)+$"〔{id}〕");
                    var version=Path.Combine(folder,"版本",runId+"-"+fingerprint[..12]);
                    var card=ExtractCard(index,id,name,library,version,bundles,sources,am,ct);
                    card.Folder=folder;card.VersionFolder=version;card.Fingerprint=fingerprint;
                    // Publish only after every output has been written and checked. Previous versions are untouched.
                    lock(stateGate)
                    {
                    state.Cards[id]=card;state.UpdatedUtc=DateTime.UtcNow;AssetIndex.Save(statePath,state);
                    var cardRoot=Inside(library,folder);
                    File.WriteAllText(Path.Combine(cardRoot,"当前版本.txt"),Path.GetRelativePath(cardRoot,Inside(library,version)),new UTF8Encoding(true));
                    File.WriteAllText(Path.Combine(cardRoot,"打开说明.md"),$"# {name}（{id}）\n\n当前提取位于 [{Path.GetFileName(version)}](版本/{Path.GetFileName(version)}/卡片说明.md)。\n\n原游戏文件、卡图、骨骼、图集和贴图在该版本中。旧版本保留；自己的修改请另外保存。\n",new UTF8Encoding(true));
                    if(previous is null)report.Added++;else report.Updated++;
                    report.TexturesWritten+=card.TextureCount;report.OriginalFilesWritten+=card.BundleCount;report.PreviewGroupsWritten+=card.PreviewCount;
                    processed++;log($"[{processed}/{ids.Count}] {name}〔{id}〕：{card.BundleCount} 文件，{card.TextureCount} 张贴图，{card.PreviewCount} 组预览素材。");
                    }
                }
                catch(Exception e) when(e is not OperationCanceledException)
                {
                    lock(stateGate){report.Failed++;processed++;report.Errors.Add($"{id} {name}: {e.GetType().Name}: {e.Message}");
                    log($"[{processed}/{ids.Count}] {name}：本卡提取未完成，已记录原因；旧版本保留。");
                    }
                }
                finally{am.UnloadAll(false);}
                lock(stateGate){report.Seconds=watch.Elapsed.TotalSeconds;AssetIndex.Save(Path.Combine(runDir,"运行摘要.json"),report);}
                return am;
            },am=>am.UnloadAll());
        report.Seconds=watch.Elapsed.TotalSeconds;
        WriteIndexes(index,library,state,report,only);
        AssetIndex.Save(Path.Combine(runDir,"运行摘要.json"),report);AssetIndex.Save(Path.Combine(library,"最近运行.json"),report);
        File.WriteAllLines(Path.Combine(runDir,"失败列表.txt"),report.Errors,new UTF8Encoding(true));
        log($"完成：新增 {report.Added}，更新 {report.Updated}，跳过 {report.Skipped}，失败 {report.Failed}；用时 {report.Seconds:0.0} 秒。");
        return report;
    }

    static CardState ExtractCard(GameIndex index,int id,string name,string library,string version,List<BundleRecord> bundles,List<SourceDigest> sources,AssetsTools.NET.Extra.AssetsManager am,CancellationToken ct)
    {
        var root=Inside(library,version);Directory.CreateDirectory(root);var assets=new List<LibraryAsset>();
        foreach(var b in bundles)
        {
            ct.ThrowIfCancellationRequested();var digest=sources.First(s=>s.Path==b.Path);var bundleId=digest.BundleId;
            var original=Path.Combine(root,"游戏关联文件",bundleId[..2],bundleId);Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(b.Path,original,false);
            if(AssetIndex.Sha(original)!=digest.Sha256)throw new IOException("源文件复制校验失败或提取期间资源发生变化："+b.Path);
            try
            {
                var bundle=am.LoadBundleFile(original,true);
                foreach(var group in b.Assets.GroupBy(a=>a.FileIndex))
                {
                    var af=am.LoadAssetsFileFromBundle(bundle,group.Key,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                    foreach(var a in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        var logical=b.Containers.FirstOrDefault(c=>Path.GetFileName(c).StartsWith(a.Name,StringComparison.OrdinalIgnoreCase))??b.Containers.FirstOrDefault()??"";
                        var info=af.file.GetAssetInfo(a.PathId);var field=am.GetBaseField(af,info);
                        var category=Category(logical,id);var extension=a.Type=="Texture2D"?".png":a.Type=="TextAsset"?(logical.EndsWith(".json",StringComparison.OrdinalIgnoreCase)?".json":a.Name.Contains("atlas",StringComparison.OrdinalIgnoreCase)?".atlas":".txt"):".assetbin";
                        var baseName=a.Name.EndsWith(".atlas",StringComparison.OrdinalIgnoreCase)?a.Name[..^6]:a.Name;
                        var relative=Path.Combine("素材",category,SafeComponent(baseName)+extension);var path=Inside(root,relative);
                        if(File.Exists(path)){relative=Path.Combine("素材",category,SafeComponent(baseName)+"-"+bundleId+"-"+a.PathId+extension);path=Inside(root,relative);}
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if(a.Type=="Texture2D")
                        {
                            var t=TextureFile.ReadTextureFile(field);t.FillPictureData(af);var raw=t.DecodeTextureRaw(t.pictureData)??throw new IOException("纹理解码失败："+a.Name);
                            using(var img=Image.LoadPixelData<Bgra32>(raw,t.m_Width,t.m_Height)){img.Mutate(c=>c.Flip(FlipMode.Vertical));img.SaveAsPng(path,Encoder);}
                            // Round-trip every exported PNG, not just a sample. Includes all alpha bytes.
                            using var check=Image.Load<Bgra32>(path);check.Mutate(c=>c.Flip(FlipMode.Vertical));
                            if(check.Width!=t.m_Width||check.Height!=t.m_Height)throw new IOException("PNG 尺寸不一致。");
                            var decoded=new byte[raw.Length];check.CopyPixelDataTo(decoded);
                            if(!raw.SequenceEqual(decoded))throw new IOException("PNG 像素/透明度回读不一致："+a.Name);
                        }
                        else if(a.Type=="TextAsset")File.WriteAllBytes(path,AssetIndex.Script(field));
                        else{af.file.Reader.Position=info.GetAbsoluteByteOffset(af.file);File.WriteAllBytes(path,af.file.Reader.ReadBytes(checked((int)info.ByteSize)));}
                        assets.Add(new(bundleId,a,logical,relative,AssetIndex.Sha(path)));
                    }
                }
            }
            finally{am.UnloadAll(false);}
        }
        var preview=LibraryPreview.Prepare(root,id,assets);
        var status=preview.Groups>0?"Spine 素材已配齐（未逐卡播放验收）":assets.Any(a=>a.Asset.Name.Contains("JS",StringComparison.OrdinalIgnoreCase))?"Spine 资源不完整或格式需另行处理":"原生 Timeline 或未发现可用 Spine 数据";
        var manifest=new{Format="card-materials-v1",CardId=id,ChineseName=name,ChineseNameSources=index.ChineseNameSources,CreatedUtc=DateTime.UtcNow,
            OriginalFiles=sources,Assets=assets,Containers=bundles.ToDictionary(b=>Path.GetFileName(b.Path),b=>b.Containers),
            Preview=preview,Verification="所有原 Bundle 副本 SHA-256 一致；每张 PNG 与来源解码像素逐字节一致（包含 Alpha）",
            Scope="当前安装中按卡号/容器直接关联的资源。原始 Bundle 保留全部内部内容；公共特效、独立音效、未下载素材和其他间接依赖不保证穷尽。",ExporterVersion};
        AssetIndex.Save(Path.Combine(root,"manifest.json"),manifest);
        var lines=new List<string>{"类别,素材名,宽,高,提取路径,原Bundle,内部路径,SHA256"};
        string Q(string x)=>"\""+x.Replace("\"","\"\"")+"\"";
        lines.AddRange(assets.Select(a=>$"{Q(a.Asset.Type)},{Q(a.Asset.Name)},{a.Asset.Width},{a.Asset.Height},{Q(a.File)},{Q(a.BundleId)},{Q(a.LogicalPath)},{a.Sha256}"));
        File.WriteAllLines(Path.Combine(root,"文件对应清单.csv"),lines,new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"卡片说明.md"),$"# {name}（{id}）\n\n- 游戏关联文件：{bundles.Count} 个原 Bundle，保留原文件名。\n- 素材：{assets.Count(a=>a.Asset.Type=="Texture2D")} 张原尺寸 PNG，及已识别的骨骼/图集等数据。\n- 外部预览素材：{preview.Groups} 组，位于“外部预览”；EX Studio 打开其中 config.json，再在动画页选择动作。预览配置是辅助文件，未逐卡播放验收。\n- 状态：{status}。\n- 文件对应清单.csv 和 manifest.json 记录每个文件的来源与校验值。\n\n本版本只提取素材。编辑时另存副本，保持图集画布、部件位置和透明边缘。游戏中镜头、粒子、材质、音效不由 Spine 查看器完整复现。公共资源与未下载素材不保证全部关联。旧版本保留。\n",new UTF8Encoding(true));
        var outputs=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(p=>{var f=new FileInfo(p);return new OutputDigest(Path.GetRelativePath(library,p),AssetIndex.Sha(p),f.Length,f.LastWriteTimeUtc.Ticks);}).ToList();
        return new CardState{Id=id,ChineseName=name,Status=status,BundleCount=bundles.Count,TextureCount=assets.Count(a=>a.Asset.Type=="Texture2D"),PreviewCount=preview.Groups,Files=outputs};
    }
    static string Category(string logical,int id)
    {
        var lower=logical.ToLowerInvariant();
        if(lower.Contains("/monstercutin/"))
        {
            var tail=Regex.Match(lower,$@"/monstercutin/(.*?)/p{id}/(.*)");
            if(tail.Success)
            {
                var dir=Path.GetDirectoryName(tail.Groups[2].Value.Replace('/',Path.DirectorySeparatorChar))??"";
                dir=dir.Replace("highend_hd","高清").Replace("sd","低清");
                return Path.Combine("召唤动画",SafeComponent(tail.Groups[1].Value),dir);
            }
            return "召唤动画";
        }
        if(lower.Contains("/illust/"))return "普通卡图";
        if(lower.Contains("/cardpack/"))return Path.Combine("卡包展示",lower.Contains("highend_hd")?"高清":"低清");
        return "其他";
    }
    public static void WriteIndexes(GameIndex index,string library,LibraryState state,BulkReport report,int[]? only=null)
    {
        var current=AssetIndex.AnimationIds(index).ToHashSet();if(only is not null)current.IntersectWith(only);
        var failedIds=report.Errors.Select(e=>int.TryParse(e.Split(' ')[0],out var n)?n:0).ToHashSet();
        var rows=current.Union(state.Cards.Keys).Order().Select(id=>new{Id=id,ChineseName=index.ChineseNames.GetValueOrDefault(id)??state.Cards.GetValueOrDefault(id)?.ChineseName??"未定位中文名",
            InCurrentGame=current.Contains(id),Card=state.Cards.GetValueOrDefault(id),Error=failedIds.Contains(id)}).ToList();
        string Csv(string x)=>"\""+x.Replace("\"","\"\"")+"\"";string H(string x)=>WebUtility.HtmlEncode(x);
        string Rel(string x)=>string.Join("/",x.Replace('\\','/').Split('/').Select(Uri.EscapeDataString));
        var csv=new List<string>{"游戏中文名,卡号,状态,原始文件数,贴图数,预览组数,当前版本目录"};
        var md=new List<string>{"# 动画卡片素材索引","",$"本次检测 {current.Count} 个卡号；新增 {report.Added}，更新 {report.Updated}，跳过 {report.Skipped}，失败 {report.Failed}。","","| 游戏中文名 | 卡号 | 状态 | 贴图 |","|---|---:|---|---:|"};
        var html=new StringBuilder("<!doctype html><html lang=zh-CN><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>游戏王动画卡片素材索引</title><style>body{font:16px 'Microsoft YaHei',sans-serif;margin:32px;background:#f3f5f8;color:#172333}input{padding:12px;width:min(600px,90%);font-size:17px}table{border-collapse:collapse;width:100%;margin-top:20px;background:white}th,td{padding:10px;text-align:left;border-bottom:1px solid #dde3eb}th{position:sticky;top:0;background:#e6edf6}a{color:#145fb0}small{color:#566575}</style><h1>游戏王动画卡片素材索引</h1>");
        html.Append($"<p>本次检测 {current.Count} 个卡号 · 新增 {report.Added} · 更新 {report.Updated} · 跳过 {report.Skipped} · 失败 {report.Failed}</p><p><small>游戏简体中文名；同名异画用卡号区分。当前安装已下载且成功识别的资源，未逐卡游戏内或播放器验收。</small></p><input id=q placeholder='搜索中文卡名或卡号' oninput=filterRows()><p id=count></p><table><thead><tr><th>游戏中文名</th><th>卡号</th><th>状态</th><th>原文件</th><th>贴图</th><th>预览组</th><th>查看</th></tr></thead><tbody>");
        foreach(var r in rows)
        {
            var c=r.Card;var status=r.Error?"本轮提取失败；已有版本保留":!r.InCurrentGame?"本次未定位，保留历史":c?.Status??"未完成";
            var version=c?.VersionFolder??"";
            csv.Add($"{Csv(r.ChineseName)},{r.Id},{Csv(status)},{c?.BundleCount??0},{c?.TextureCount??0},{c?.PreviewCount??0},{Csv(version)}");
            var label=r.ChineseName.Replace("|","\\|");var link=c is null?label:$"[{label}]({Rel(Path.Combine(version,"卡片说明.md"))})";
            md.Add($"| {link} | {r.Id} | {status} | {c?.TextureCount??0} |");
            html.Append($"<tr><td>{H(r.ChineseName)}</td><td>{r.Id}</td><td>{H(status)}</td><td>{c?.BundleCount??0}</td><td>{c?.TextureCount??0}</td><td>{c?.PreviewCount??0}</td><td>");
            if(c is not null)html.Append($"<a href='{Rel(version)}/'>文件夹</a> · <a href='{Rel(Path.Combine(version,"文件对应清单.csv"))}'>清单</a>");
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><script>function filterRows(){let q=document.getElementById('q').value.trim().toLowerCase(),n=0;document.querySelectorAll('tbody tr').forEach(r=>{let ok=r.innerText.toLowerCase().includes(q);r.hidden=!ok;if(ok)n++});document.getElementById('count').innerText='显示 '+n+' 张卡'}filterRows()</script></html>");
        File.WriteAllLines(Path.Combine(library,"索引目录.csv"),csv,new UTF8Encoding(true));File.WriteAllLines(Path.Combine(library,"索引目录.md"),md,new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(library,"索引目录.html"),html.ToString(),new UTF8Encoding(false));
        AssetIndex.Save(Path.Combine(library,"索引目录.json"),rows.Select(r=>new{r.Id,r.ChineseName,r.InCurrentGame,Failed=r.Error,State=r.Card}));
        File.WriteAllLines(Path.Combine(library,"扫描错误.txt"),index.Errors,new UTF8Encoding(true));
    }
}
