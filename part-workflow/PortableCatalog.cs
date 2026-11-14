using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CardAssetWorkbench;

namespace PartWorkflow;
public record CatalogVariant(string Variant,string SkeletonFile,string AtlasFile,LibraryAsset Skeleton,LibraryAsset Atlas,List<LibraryAsset> Pages);
public static class PortableCatalog
{
    public static string Family(string path){var m=Regex.Match(path,@"^(.*?/(?:highend_hd|sd))(?:/|$)",RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value.ToLowerInvariant():Path.GetDirectoryName(path)?.ToLowerInvariant()??path;}
    public static void BuildSeed(string source,string target)
    {
        if(Path.GetFullPath(source).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("目录生成目标不能是原素材库");
        Directory.CreateDirectory(target);var state=JsonFile.Read<LibraryState>(Path.Combine(source,"library-state.json"));
        foreach(var card in state.Cards.Values)
        {
            var original=Path.Combine(source,card.VersionFolder);var dst=Path.Combine(target,card.VersionFolder);Directory.CreateDirectory(dst);
            var m=JsonNode.Parse(File.ReadAllText(Path.Combine(original,"manifest.json")))!;
            var assets=m["Assets"]!.Deserialize<List<LibraryAsset>>()!;var variants=new List<CatalogVariant>();
            foreach(var c in m["Preview"]!["Configurations"]!.AsArray())
            {
                var path=Path.Combine(original,c!.GetValue<string>());var config=JsonNode.Parse(File.ReadAllText(path))!;var dir=Path.GetDirectoryName(path)!;
                var label=Path.GetFileName(dir);var atlasName=config["atlases"]![0]!["atlas"]!.GetValue<string>();var skeletonName=config["skeleton"]!.GetValue<string>();
                var atlasSha=ProjectStore.Hash(Path.Combine(dir,atlasName));var group=label.Contains("低清")?"/sd/":"/highend_hd/";
                string Label(LibraryAsset a){var match=Regex.Match(a.LogicalPath,@"/monstercutin/(.*?)/p\d+/(.*)",RegexOptions.IgnoreCase);return(match.Groups[1].Value+"-"+Path.GetDirectoryName(match.Groups[2].Value)!.Replace('\\','-').Replace('/','-')).Replace("highend_hd","高清").Replace("sd","低清");}
                var matching=assets.Where(a=>a.Asset.Type=="TextAsset"&&a.Sha256==atlasSha&&a.LogicalPath.Contains(group,StringComparison.OrdinalIgnoreCase)).ToArray();
                var atlas=matching.First(a=>Label(a)+"-"+a.BundleId==label||Label(a)==label);
                var family=Family(atlas.LogicalPath);var skeletonSha=ProjectStore.Hash(Path.Combine(dir,skeletonName));var skeleton=assets.Single(a=>a.Asset.Type=="TextAsset"&&a.LogicalPath.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&a.Sha256==skeletonSha&&Family(a.LogicalPath)==family);
                var pages=config["atlases"]![0]!["textures"]!.AsArray().Select(p=>p!.GetValue<string>()).Select(n=>assets.Single(a=>a.Asset.Type=="Texture2D"&&a.Asset.Name.Equals(Path.GetFileNameWithoutExtension(n),StringComparison.OrdinalIgnoreCase)&&Path.GetDirectoryName(a.LogicalPath)!.Equals(Path.GetDirectoryName(atlas.LogicalPath),StringComparison.OrdinalIgnoreCase))).ToList();
                variants.Add(new(Path.Combine("模型",label,"model.config.json"),skeletonName,atlasName,skeleton,atlas,pages));
            }
            WriteCard(target,card,m,variants);
        }
        JsonFile.Write(Path.Combine(target,"library-state.json"),state);JsonFile.Write(Path.Combine(target,"catalog-only.json"),new{Format="md-catalog-only-v1",Cards=state.Cards.Count});
        var indexPath=Path.Combine(Path.GetDirectoryName(source)!,"索引","index.json");if(File.Exists(indexPath))BulkLibrary.WriteIndexes(AssetIndex.Load(indexPath),target,state,new BulkReport{Detected=state.Cards.Count,Skipped=state.Cards.Count});
        var hashes=Path.Combine(source,"source-hashes.json");if(File.Exists(hashes))File.Copy(hashes,Path.Combine(target,"source-hashes.json"),true);
        File.WriteAllText(Path.Combine(target,"目录说明.txt"),"这里只保存卡名、来源和资源映射。游戏贴图、骨骼和原Bundle均不随包携带；打开卡片时才从所选游戏目录临时提取。\r\n",new UTF8Encoding(true));
    }
    static void WriteCard(string library,CardState card,JsonNode manifest,List<CatalogVariant> variants)
    {
        var root=Path.Combine(library,card.VersionFolder);Directory.CreateDirectory(root);
        manifest["CatalogOnly"]=true;manifest["CatalogVariants"]=JsonSerializer.SerializeToNode(variants);
        manifest["Preview"]=JsonSerializer.SerializeToNode(new{Groups=variants.Count,Configurations=variants.Select(v=>v.Variant).ToArray(),Notes=new[]{"资源按需从本机游戏提取，不包含外部预览或预提取贴图。"}});
        manifest["Verification"]="目录元数据，使用时读取当前安装资源并核对提取结果。";
        var file=Path.Combine(root,"manifest.json");File.WriteAllText(file,manifest.ToJsonString(JsonFile.Options));var fi=new FileInfo(file);
        File.WriteAllText(Path.Combine(root,"卡片说明.md"),$"# {card.ChineseName}〔{card.Id}〕\n\n目录已收录。请从程序打开卡片，所需模型与贴图会从本机游戏临时提取。\n");
        string Q(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
        File.WriteAllLines(Path.Combine(root,"文件对应清单.csv"),new[]{"类型,资源名,Bundle,游戏内部路径"}.Concat(manifest["Assets"]!.Deserialize<List<LibraryAsset>>()!.Select(a=>$"{Q(a.Asset.Type)},{Q(a.Asset.Name)},{Q(a.BundleId)},{Q(a.LogicalPath)}")),new UTF8Encoding(true));
        card.Files=[new(Path.GetRelativePath(library,file),ProjectStore.Hash(file),fi.Length,fi.LastWriteTimeUtc.Ticks)];card.PreviewCount=variants.Count;
        card.Status=variants.Count>0?"目录已收录（资源按需提取）":"目录已收录（无可编辑Spine配置）";
        Directory.CreateDirectory(Path.Combine(library,card.Folder));File.WriteAllText(Path.Combine(library,card.Folder,"打开说明.md"),$"# {card.ChineseName}〔{card.Id}〕\n\n请在MD动画替换助手中选择此卡，程序按需读取游戏资源。\n");
    }
    public static BulkReport Update(GameIndex index,string library,Action<string> log,CancellationToken token,int[]? only=null)
    {
        using var runLock=new FileStream(Path.Combine(library,".运行锁"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var state=JsonFile.Read<LibraryState>(Path.Combine(library,"library-state.json"));var ids=AssetIndex.AnimationIds(index);if(only!=null)ids=ids.Intersect(only).ToList();
        var report=new BulkReport{RunId=DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),Detected=ids.Count};var run=Path.Combine(library,"运行记录",report.RunId);Directory.CreateDirectory(run);File.Copy(Path.Combine(library,"library-state.json"),Path.Combine(run,"更新前状态.json"));
        var associations=BulkLibrary.BuildAssociations(index,ids);var hashesPath=Path.Combine(library,"source-hashes.json");var hashes=File.Exists(hashesPath)?JsonFile.Read<Dictionary<string,HashCacheEntry>>(hashesPath):[];
        var digests=new Dictionary<string,SourceDigest>();var watch=System.Diagnostics.Stopwatch.StartNew();
        foreach(var b in associations.Values.SelectMany(b=>b).DistinctBy(b=>b.Path))
        {
            token.ThrowIfCancellationRequested();var f=new FileInfo(b.Path);var sha=hashes.TryGetValue(b.Path,out var h)&&h.Length==f.Length&&h.ModifiedTicks==f.LastWriteTimeUtc.Ticks?h.Sha256:ProjectStore.Hash(b.Path);
            hashes[b.Path]=new(f.Length,f.LastWriteTimeUtc.Ticks,sha);digests[b.Path]=new(Path.GetFileName(b.Path),b.Path,sha,f.Length,f.LastWriteTimeUtc.Ticks);
        }
        using var manager=new ManagerScope();
        foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(!index.ChineseNames.TryGetValue(id,out var name))throw new InvalidDataException("没有游戏中文卡名，未猜测名称");
                var sources=associations[id].Select(b=>digests[b.Path]).ToList();var fingerprint=BulkLibrary.Fingerprint(id,name,sources);var previous=state.Cards.GetValueOrDefault(id);
                if(previous!=null&&previous.PreviewCount>0&&previous.Fingerprint==fingerprint&&previous.Files.All(f=>File.Exists(Path.Combine(library,f.File))&&new FileInfo(Path.Combine(library,f.File)).Length==f.Length)){report.Skipped++;continue;}
                var assets=new List<LibraryAsset>();foreach(var b in associations[id])foreach(var a in b.Assets){var logical=b.Containers.FirstOrDefault(c=>Path.GetFileName(c).StartsWith(a.Name,StringComparison.OrdinalIgnoreCase))??b.Containers.FirstOrDefault()??"";assets.Add(new(Path.GetFileName(b.Path),a,logical,"", ""));}
                var variants=Describe(index,assets,manager,token);var folder=Path.Combine("卡片",BulkLibrary.SafeComponent(name)+$"〔{id}〕");
                var card=new CardState{Id=id,ChineseName=name,Folder=folder,VersionFolder=Path.Combine(folder,"版本",report.RunId+"-"+fingerprint[..12]),Fingerprint=fingerprint,BundleCount=sources.Count,TextureCount=assets.Count(a=>a.Asset.Type=="Texture2D")};
                var manifest=JsonSerializer.SerializeToNode(new{Format="card-materials-v1",CardId=id,ChineseName=name,OriginalFiles=sources,Assets=assets,CreatedUtc=DateTime.UtcNow})!;
                WriteCard(library,card,manifest,variants);state.Cards[id]=card;if(previous==null)report.Added++;else report.Updated++;
                state.GameRoot=index.GameRoot;state.UpdatedUtc=DateTime.UtcNow;JsonFile.Write(Path.Combine(library,"library-state.json"),state);log($"{name}〔{id}〕目录已更新，未导出贴图。");
            }
            catch(Exception ex) when(ex is not OperationCanceledException){report.Failed++;report.Errors.Add(id+": "+ex.Message);log("目录更新失败："+id+" "+ex.Message);}
        }
        state.GameRoot=index.GameRoot;state.UpdatedUtc=DateTime.UtcNow;JsonFile.Write(Path.Combine(library,"library-state.json"),state);
        JsonFile.Write(hashesPath,hashes);report.Seconds=watch.Elapsed.TotalSeconds;BulkLibrary.WriteIndexes(index,library,state,report,only);JsonFile.Write(Path.Combine(run,"更新结果.json"),report);return report;
    }
    static List<CatalogVariant> Describe(GameIndex index,List<LibraryAsset> assets,ManagerScope manager,CancellationToken token)
    {
        var result = new List<CatalogVariant>();
        var paths = new Regex(@"/monstercutin/(.*?)/p\d+/((?:highend_hd|sd)/.+)$", RegexOptions.IgnoreCase);
        var modelAssets = assets.Where(a => paths.IsMatch(a.LogicalPath)).ToArray();
        var textures = modelAssets.Where(a => a.Asset.Type == "Texture2D").ToArray();
        var textAssets = modelAssets.Where(a => a.Asset.Type == "TextAsset").ToArray();
        var atlases = textAssets.Where(a => a.Asset.Name.Contains(".atlas", StringComparison.OrdinalIgnoreCase)).ToArray();
        var skeletons = new List<LibraryAsset>();
        foreach (var asset in textAssets.Except(atlases))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var json = JsonDocument.Parse(manager.Text(index, asset));
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("bones", out _) && root.TryGetProperty("animations", out _))
                    skeletons.Add(asset);
            }
            catch (JsonException) { }
        }
        foreach (var family in atlases.GroupBy(a => Family(a.LogicalPath)))
        {
            var standalone = family.Where(a => a.LogicalPath.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)
                || a.LogicalPath.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)).ToArray();
            var localSkeletons = skeletons.Where(s => Family(s.LogicalPath) == family.Key).ToArray();
            var localTextures = textures.Where(t => Family(t.LogicalPath) == family.Key).ToArray();
            var foundStandalone = false;
            foreach (var atlas in standalone)
                foundStandalone |= TryAdd(atlas, localSkeletons, localTextures, embedded: false);
            if (foundStandalone) continue;

            // The prefab can contain all three asset types. Prefer it over duplicate payloads
            // also embedded in other bundles, and only accept a unique object per page.
            foreach (var atlas in family.Except(standalone).OrderByDescending(a => a.LogicalPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                if (TryAdd(atlas, localSkeletons, localTextures, embedded: true)) break;
        }
        return result;

        bool TryAdd(LibraryAsset atlas, LibraryAsset[] candidates, LibraryAsset[] pageCandidates, bool embedded)
        {
            token.ThrowIfCancellationRequested();
            var sameBundle = candidates.Where(a => a.BundleId == atlas.BundleId).ToArray();
            var separate = candidates.Where(a => a.LogicalPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            var skeletonChoices = sameBundle.Length > 0 || embedded ? sameBundle : separate.Length > 0 ? separate : candidates;
            if (skeletonChoices.Length != 1) return false;

            var pageNames = LibraryPreview.AtlasPages(Encoding.UTF8.GetString(manager.Text(index, atlas)));
            if (pageNames.Count == 0) return false;
            var pages = new List<LibraryAsset>();
            foreach (var name in pageNames)
            {
                var matches = pageCandidates.Where(a =>
                    a.Asset.Name.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase) &&
                    (embedded ? a.BundleId == atlas.BundleId :
                        Path.GetDirectoryName(a.LogicalPath)!.Equals(Path.GetDirectoryName(atlas.LogicalPath), StringComparison.OrdinalIgnoreCase)))
                    .DistinctBy(a => (a.BundleId, a.Asset.FileIndex, a.Asset.PathId)).ToArray();
                if (matches.Length != 1) return false;
                pages.Add(matches[0]);
            }

            var match = paths.Match(atlas.LogicalPath);
            var directory = Path.GetDirectoryName(match.Groups[2].Value)!.Replace('\\', '-').Replace('/', '-');
            var label = (match.Groups[1].Value + "-" + directory).Replace("highend_hd", "高清", StringComparison.OrdinalIgnoreCase)
                .Replace("sd", "低清", StringComparison.OrdinalIgnoreCase);
            var variant = Path.Combine("模型", label, "model.config.json");
            if (result.Any(v => v.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase)))
                variant = Path.Combine("模型", label + "-" + atlas.BundleId, "model.config.json");
            var skeleton = skeletonChoices[0];
            var skeletonFile = skeleton.Asset.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? skeleton.Asset.Name : skeleton.Asset.Name + ".json";
            var atlasFile = atlas.Asset.Name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)
                ? atlas.Asset.Name : atlas.Asset.Name + ".atlas";
            result.Add(new(variant, skeletonFile, atlasFile, skeleton, atlas, pages));
            return true;
        }
    }
    sealed class ManagerScope:IDisposable
    {
        public readonly AssetsTools.NET.Extra.AssetsManager Manager=AssetIndex.Manager();
        public byte[] Text(GameIndex index,LibraryAsset a){var b=index.Bundles.Single(b=>Path.GetFileName(b.Path)==a.BundleId);var bundle=Manager.LoadBundleFile(b.Path,true);try{var file=Manager.LoadAssetsFileFromBundle(bundle,a.Asset.FileIndex,false);Manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);return AssetIndex.Script(Manager.GetBaseField(file,file.file.GetAssetInfo(a.Asset.PathId)));}finally{Manager.UnloadAll(false);}}
        public void Dispose()=>Manager.UnloadAll();
    }
}
