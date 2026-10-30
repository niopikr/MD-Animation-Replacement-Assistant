using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using AssetsTools.NET.Texture;

namespace PartWorkflow;
public static class OnDemandLibrary
{
    static readonly object Gate=new();
    public static string CacheRoot(string root)=>Path.Combine(AppEnvironment.Runtime(root),"素材缓存");
    public static string Ensure(string root,string catalogVersion,string variant)
    {
        var source=Path.Combine(AppEnvironment.Library(root),catalogVersion);var m=JsonNode.Parse(File.ReadAllText(Path.Combine(source,"manifest.json")))!;
        if(m["CatalogOnly"]?.GetValue<bool>()!=true)return source;
        lock(Gate)
        {
            var descriptors=m["CatalogVariants"]!.Deserialize<List<CatalogVariant>>()!;var descriptor=descriptors.Single(d=>d.Variant==variant);
            var originals=m["OriginalFiles"]!.Deserialize<List<SourceDigest>>()!;var live=new Dictionary<string,(string Path,SourceDigest Digest)>();
            foreach(var original in originals)
            {
                var path=AppEnvironment.MapGamePath(root,original.Path);if(!File.Exists(path))continue;var f=new FileInfo(path);
                live[original.BundleId]=(path,new(original.BundleId,path,ProjectStore.Hash(path),f.Length,f.LastWriteTimeUtc.Ticks));
            }
            var stamp=ProjectStore.HashBytes(Encoding.UTF8.GetBytes(catalogVersion+"\n"+string.Join('\n',live.OrderBy(p=>p.Key).Select(p=>p.Key+":"+p.Value.Digest.Path+":"+p.Value.Digest.Sha256))));
            var cache=Path.Combine(CacheRoot(root),m["CardId"]!.GetValue<int>().ToString(),stamp[..20]);Directory.CreateDirectory(cache);
            var model=ProjectStore.Under(cache,variant);if(File.Exists(model))return cache;
            var manifestPath=Path.Combine(cache,"manifest.json");var current=File.Exists(manifestPath)?JsonNode.Parse(File.ReadAllText(manifestPath))!:m.DeepClone();
            current["OriginalFiles"]=JsonSerializer.SerializeToNode(originals.Select(o=>live.TryGetValue(o.BundleId,out var l)?l.Digest:o).ToArray());
            current["MaterializedFromCurrentGame"]=true;
            var assets=current["Assets"]!.Deserialize<List<LibraryAsset>>()!;var modelDir=Path.GetDirectoryName(model)!;Directory.CreateDirectory(modelDir);
            var selected=new[]{descriptor.Skeleton,descriptor.Atlas}.Concat(descriptor.Pages).ToArray();
            var manager=AssetIndex.Manager();
            try
            {
                foreach(var group in selected.GroupBy(a=>a.BundleId))
                {
                    if(!live.TryGetValue(group.Key,out var input))throw new FileNotFoundException("当前账号尚未下载所选动画资源，请先在游戏中查看这张卡，或选择另一画质："+group.Key);
                    var original=ProjectStore.Under(cache,Path.Combine("游戏关联文件",group.Key[..2],group.Key));Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    if(!File.Exists(original)){File.Copy(input.Path,original);if(ProjectStore.Hash(original)!=input.Digest.Sha256)throw new IOException("提取时游戏资源发生变化，请重试");}
                    var bundle=manager.LoadBundleFile(original,true);
                    foreach(var item in group)
                    {
                        var af=manager.LoadAssetsFileFromBundle(bundle,item.Asset.FileIndex,false);manager.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                        var field=manager.GetBaseField(af,af.file.GetAssetInfo(item.Asset.PathId));
                        if(field["m_Name"].AsString!=item.Asset.Name)throw new InvalidDataException("游戏已更新，资源标识改变，请先更新卡片目录。");
                        var filename=item==descriptor.Skeleton?descriptor.SkeletonFile:item==descriptor.Atlas?descriptor.AtlasFile:item.Asset.Name+".png";
                        if(Path.GetFileName(filename)!=filename)throw new InvalidDataException("资源文件名包含不支持的路径");
                        var dst=ProjectStore.Under(modelDir,filename);
                        if(item.Asset.Type=="Texture2D")
                        {
                            var tex=TextureFile.ReadTextureFile(field);tex.FillPictureData(af);var pixels=tex.DecodeTextureRaw(tex.pictureData)??throw new InvalidDataException("贴图无法解码");
                            using var bgra=Image.LoadPixelData<Bgra32>(pixels,tex.m_Width,tex.m_Height);bgra.Mutate(c=>c.Flip(FlipMode.Vertical));using var rgba=bgra.CloneAs<Rgba32>();JsonFile.Png(dst,rgba);
                        }
                        else File.WriteAllBytes(dst,AssetIndex.Script(field));
                        var updated=item with{File=Path.GetRelativePath(cache,dst),Sha256=ProjectStore.Hash(dst)};var i=assets.FindIndex(a=>a.BundleId==item.BundleId&&a.Asset.PathId==item.Asset.PathId&&a.Asset.FileIndex==item.Asset.FileIndex);if(i<0)throw new InvalidDataException("目录中缺少资源映射");assets[i]=updated;
                    }
                    manager.UnloadAll(false);
                }
            }
            finally{manager.UnloadAll();}
            var pages=LibraryPreview.AtlasPages(File.ReadAllText(Path.Combine(modelDir,descriptor.AtlasFile)));
            foreach(var page in pages)if(!File.Exists(Path.Combine(modelDir,page)))throw new InvalidDataException("目录缺少动画贴图页，请先更新目录："+page);
            current["Assets"]=JsonSerializer.SerializeToNode(assets);File.WriteAllText(manifestPath,current.ToJsonString(JsonFile.Options));
            JsonFile.Write(model,new{skeleton=descriptor.SkeletonFile,atlases=new[]{new{atlas=descriptor.AtlasFile,textures=pages,tex_names=pages.Select(Path.GetFileNameWithoutExtension).ToArray()}}});
            return cache;
        }
    }
    public static object ClearUnused(ProjectStore store)
    {
        lock(Gate)
        {
            var root=CacheRoot(store.Workspace);if(!Directory.Exists(root))return new{Removed=0,Bytes=0L};
            var used=Directory.Exists(store.Root)?Directory.GetFiles(store.Root,"project.json",SearchOption.AllDirectories).Select(f=>store.RebaseSourceVersion(JsonFile.Read<ProjectState>(f).SourceVersion)).ToHashSet(StringComparer.OrdinalIgnoreCase):new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed=0;long bytes=0;
            foreach(var parent in Directory.GetDirectories(root))foreach(var folder in Directory.GetDirectories(parent))
            {
                if(used.Contains(folder))continue;
                var absolute=Path.GetFullPath(folder);if(!absolute.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("缓存路径越界");
                bytes+=Directory.GetFiles(absolute,"*",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length);Directory.Delete(absolute,true);removed++;
            }
            return new{Removed=removed,Bytes=bytes};
        }
    }
}
