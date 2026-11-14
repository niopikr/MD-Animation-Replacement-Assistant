using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

public sealed class GameReplacementExporter(ProjectStore store, Exporter preview)
{
    public record Locator(string BundleId, int FileIndex, long PathId, string Name, string Type);
    public record Edit(Locator Asset, string? Png, byte[]? Text);
    public object Export(ProjectState p, bool compatible = false)
    {
        if(p.Parts.Any(x=>x.Status=="pending"))throw new InvalidOperationException("全部部件确认后才能导出替换文件");
        var library=AppEnvironment.Library(store.Workspace);
        var version=Path.GetFullPath(p.SourceVersion);
        if(!version.StartsWith(Path.GetFullPath(library)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&!version.StartsWith(Path.GetFullPath(OnDemandLibrary.CacheRoot(store.Workspace))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("项目来源目录越界");
        var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(version,"manifest.json")))!;
        var gameRoot=new InstallSettings(store.Workspace).Read().GameRoot;
        if(gameRoot.Length==0)throw new InvalidOperationException("请先在首页选择游戏目录。");
        var assets=manifest["Assets"]!.AsArray().Select(x=>x!).ToArray();
        var intended=manifest["CatalogVariants"]?.AsArray().FirstOrDefault(v=>v!["Variant"]!.GetValue<string>()==p.Variant)?["Atlas"]?["LogicalPath"]?.GetValue<string>();
        var atlasAsset=assets.Single(a=>a["Asset"]!["Type"]!.GetValue<string>()=="TextAsset"&&a["Sha256"]!.GetValue<string>()==p.SourceHashes[p.AtlasFile]&&(intended==null||a["LogicalPath"]!.GetValue<string>()==intended));
        var family=Path.GetDirectoryName(atlasAsset["LogicalPath"]!.GetValue<string>())!;
        Locator Locate(JsonNode a)=>new(a["BundleId"]!.GetValue<string>(),a["Asset"]!["FileIndex"]!.GetValue<int>(),a["Asset"]!["PathId"]!.GetValue<long>(),a["Asset"]!["Name"]!.GetValue<string>(),a["Asset"]!["Type"]!.GetValue<string>());
        var atlas=Atlas.Parse(File.ReadAllText(Path.Combine(store.DirectoryFor(p.Id),"source",p.AtlasFile)));
        var changedPages=p.Parts.Where(x=>x.Status is "replaced" or "empty").Select(x=>x.Page).Distinct().ToArray();
        if(changedPages.Length==0)changedPages=atlas.Pages.Keys.ToArray();
        var pageAssets=changedPages.ToDictionary(n=>n,n=>assets.Single(a=>a["Asset"]!["Type"]!.GetValue<string>()=="Texture2D"&&
            a["Asset"]!["Name"]!.GetValue<string>()==Path.GetFileNameWithoutExtension(n)&&Path.GetDirectoryName(a["LogicalPath"]!.GetValue<string>())==family&&a["Sha256"]!.GetValue<string>()==p.SourceHashes[n]));
        var ids=pageAssets.Values.Select(a=>Locate(a).BundleId).Append(Locate(atlasAsset).BundleId).Distinct().ToArray();
        var inputs=ids.Select(id=>{
            if(id.Length!=8||id.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("无效Bundle名称");
            var original=manifest["OriginalFiles"]!.AsArray().Single(x=>x!["BundleId"]!.GetValue<string>()==id)!;
            var live=AppEnvironment.MapGamePath(store.Workspace,original["Path"]!.GetValue<string>());
            var accountRoot=Path.Combine(gameRoot,"LocalData",new InstallSettings(store.Workspace).Read().Account);
            var relative=Path.GetRelativePath(accountRoot,live);
            if(!relative.Replace('\\','/').StartsWith("0000/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("该资源不在所选账号的0000目录，无法生成此精简替换结构："+live);
            if(Path.GetFileName(live)!=id)throw new InvalidDataException("Bundle目标文件名不一致");
            var archived=ProjectStore.Under(version,Path.Combine("游戏关联文件",id[..2],id));
            var hash=original["Sha256"]!.GetValue<string>();
            var liveHash=File.Exists(live)?ProjectStore.Hash(live):"";
            var knownPrevious=compatible&&p.Exports.Any(e=>{
                var record=ProjectStore.Under(store.DirectoryFor(p.Id),e+"/替换清单.json");if(!File.Exists(record))return false;
                var prior=JsonNode.Parse(File.ReadAllText(record))!;
                return prior["Files"]!.AsArray().Any(f=>f!["Target"]!.GetValue<string>()==live&&f["ReplacementSha256"]!.GetValue<string>()==liveHash);
            });
            if(!File.Exists(live)||(liveHash!=hash&&!knownPrevious)||ProjectStore.Hash(archived)!=hash)
                throw new InvalidDataException("游戏原文件缺失或已不同于提取版本，请先更新素材库并新建项目："+live);
            return new{Id=id,Relative=relative,Live=live,Archived=archived,Hash=hash,LiveHash=liveHash};
        }).ToArray();
        // Work only in a fresh export. Original game files are opened read-only for backup and hash checks.
        var snapshot=JsonSerializer.SerializeToNode(preview.Export(p,true,gamePackage:true))!;
        var folder=snapshot["Folder"]!.GetValue<string>();var model=Path.Combine(folder,"游戏图集核对");Directory.CreateDirectory(model);
        File.Copy(Path.Combine(store.DirectoryFor(p.Id),"source",p.SkeletonFile),Path.Combine(model,p.SkeletonFile));
        var edits=new List<Edit>();
        if(compatible)edits=CompatibleTexture.Prepare(store,p,atlas,model,pageAssets.ToDictionary(a=>a.Key,a=>Locate(a.Value)));
        else foreach(var page in atlas.Pages.Keys)
        {
            if(!changedPages.Contains(page)){File.Copy(Path.Combine(store.DirectoryFor(p.Id),"source",page),Path.Combine(model,page));continue;}
            var size=atlas.Pages[page]["size"].Split(',').Select(int.Parse).ToArray();var width=size[0];var oldHeight=size[1];
            // Append isolated rectangles below the original page. Keep page names/order and all material references.
            var layouts=new List<(Part Part,string Region,int X,int Y,int W,int H)>();var x=2;var y=oldHeight+2;var row=0;
            var skeleton=JsonNode.Parse(File.ReadAllText(Path.Combine(store.DirectoryFor(p.Id),"source",p.SkeletonFile)))!;
            foreach(var part in p.Parts.Where(a=>a.Page==page&&a.Status is "replaced" or "empty"))
            {
                var w=part.AtlasOffsets[2];var h=part.AtlasOffsets[3];
                width=Math.Max(width,w+4);
                if(x+w+2>width){x=2;y+=row+4;row=0;}
                var region=part.RegionName;
                if(region.Length==0)region=skeleton["skins"]!.AsArray().Select(s=>s?["attachments"]?[part.Slot]?[part.Name]).Where(a=>a!=null).Select(a=>a!["path"]?.GetValue<string>()??part.Name).First();
                layouts.Add((part,region,x,y,w,h));x+=w+4;row=Math.Max(row,h);
            }
            var height=y+row+2;
            if(width>8192||height>8192)throw new InvalidOperationException("替换图集超过8192像素，请减少本次替换部件或选择更低画质。");
            using var canvas=new Image<Rgba32>(width,height);
            using(var original=Image.Load<Rgba32>(Path.Combine(store.DirectoryFor(p.Id),"source",page)))
            {
                // Normalize to the atlas logical dimensions when Unity imported a scaled Texture2D.
                if(original.Width!=size[0]||original.Height!=size[1])original.Mutate(c=>c.Resize(size[0],size[1]));
                Copy(original,canvas,0,0);
            }
            foreach(var l in layouts)
            {
                var revision=JsonFile.Read<Revision>(ProjectStore.Under(store.DirectoryFor(p.Id),l.Part.ConfirmedRevision));
                using var image=Image.Load<Rgba32>(ProjectStore.Under(store.DirectoryFor(p.Id),revision.Aligned));
                if(image.Width!=l.W||image.Height!=l.H)image.Mutate(c=>c.Resize(l.W,l.H));
                if(l.Part.Pma)Atlas.ToPma(image);Copy(image,canvas,l.X,l.Y);
                atlas.Regions[l.Region]=new Atlas.Region{Name=l.Region,Page=page,Fields=new(){["bounds"]=$"{l.X},{l.Y},{l.W},{l.H}",["offsets"]=$"0,0,{l.W},{l.H}",["rotate"]="0"}};
            }
            atlas.Pages[page]["size"]=$"{width},{height}";
            var png=Path.Combine(model,page);JsonFile.Png(png,canvas);edits.Add(new(Locate(pageAssets[page]),png,null));
        }
        var text=new StringBuilder();foreach(var page in atlas.Pages){text.AppendLine(page.Key);foreach(var f in page.Value)text.AppendLine($"{f.Key}: {f.Value}");foreach(var r in atlas.Regions.Values.Where(r=>r.Page==page.Key)){text.AppendLine(r.Name);foreach(var f in r.Fields)text.AppendLine($"  {f.Key}: {f.Value}");}text.AppendLine();}
        var atlasBytes=new UTF8Encoding(false).GetBytes(text.ToString());
        if(!compatible){File.WriteAllBytes(Path.Combine(model,p.AtlasFile),atlasBytes);edits.Add(new(Locate(atlasAsset),null,atlasBytes));}
        var records=new List<object>();
        var compatibilityChecks=new List<object>();
        foreach(var input in inputs)
        {
            var backup=ProjectStore.Under(Path.Combine(folder,"原文件备份"),input.Relative);Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Copy(input.Archived,backup);
            if(ProjectStore.Hash(backup)!=input.Hash)throw new IOException("备份校验失败");
            if(input.LiveHash!=input.Hash){var before=ProjectStore.Under(Path.Combine(folder,"替换前文件备份"),input.Relative);Directory.CreateDirectory(Path.GetDirectoryName(before)!);File.Copy(input.Live,before);if(ProjectStore.Hash(before)!=input.LiveHash)throw new IOException("替换前文件备份失败");}
            var output=ProjectStore.Under(Path.Combine(folder,"替换文件"),input.Relative);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(backup,output);
            var bundleEdits=edits.Where(e=>e.Asset.BundleId==input.Id).ToArray();
            if(compatible){if(bundleEdits.Length>0)compatibilityChecks.Add(CompatibleTexture.Rewrite(output,bundleEdits));}
            else Rewrite(output,bundleEdits);
            if(ProjectStore.Hash(input.Live)!=input.LiveHash)throw new IOException("导出期间原游戏文件发生变化，请重试");
            records.Add(new{File=input.Relative,Target=input.Live,OriginalSha256=input.Hash,BeforeExportSha256=input.LiveHash,ReplacementSha256=ProjectStore.Hash(output),Backup=Path.GetRelativePath(folder,backup),Replacement=Path.GetRelativePath(folder,output)});
        }
        JsonFile.Write(Path.Combine(folder,"替换清单.json"),new{Version=2,p.CardId,p.ChineseName,p.Variant,GameRoot=gameRoot,Files=records,Verified=true,CompatibleOriginalFormat=compatible,GameModified=false});
        if(compatible)JsonFile.Write(Path.Combine(folder,"原格式兼容验证.json"),new{AtlasByteIdentical=ProjectStore.Hash(Path.Combine(model,p.AtlasFile))==p.SourceHashes[p.AtlasFile],Checks=compatibilityChecks});
        File.WriteAllText(Path.Combine(folder,"替换说明.txt"),$"{p.ChineseName}〔{p.CardId}〕\r\n适用版本/画质：{p.Variant}\r\n\r\n关闭游戏。将 替换文件 内的目录结构合并复制到：\r\n{Path.Combine(gameRoot,"LocalData",new InstallSettings(store.Workspace).Read().Account)}\r\n同名文件选择替换。不要把整个项目目录复制进游戏。\r\n\r\n恢复：关闭游戏，将 原文件备份 内的目录结构复制回上述账号目录并替换。\r\n原文件备份为提取时保留的游戏文件快照（可能含此前安装的修改）；已安装旧包时另存替换前文件备份。SHA-256见替换清单.json。\r\n本程序只生成文件，未安装到游戏。只针对所选地区/画质；其他画质不会自动改变。游戏更新可能恢复资源。\r\n已完成Bundle重新读取、Texture2D像素、Atlas内容和其他对象字节验证；仍需游戏内确认动作显示。\r\n",new UTF8Encoding(true));
        File.Copy(Path.Combine(folder,"先看这里.txt"),Path.Combine(folder,"动画模型","预览说明.txt"));
        File.Copy(Path.Combine(folder,"替换说明.txt"),Path.Combine(folder,"先看这里.txt"),true);
        var report=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"导出记录.json")))!;
        report["PackageType"]="游戏同名Bundle替换文件，附原文件备份及辅助Spine预览";
        report["GameReplacementReport"]="替换清单.json";report["GameBundlesVerified"]=true;
        File.WriteAllText(Path.Combine(folder,"导出记录.json"),report.ToJsonString(JsonFile.Options));
        File.WriteAllText(Path.Combine(folder,"替换文件完成标记.txt"),"同名Bundle与原文件备份均已生成并验证。\r\n");
        return new{Folder=folder,Relative=snapshot["Relative"]!.GetValue<string>(),Config=snapshot["Config"]!.GetValue<string>(),Changes=p.Parts.Count(x=>x.Status is "replaced" or "empty"),BundleCount=records.Count,IsFinal=true};
    }
    static void Copy(Image<Rgba32> source,Image<Rgba32> target,int dx,int dy){for(var y=0;y<source.Height;y++)for(var x=0;x<source.Width;x++)target[x+dx,y+dy]=source[x,y];}
    static AssetsManager Manager(){var am=new AssetsManager();am.LoadClassPackage(AppEnvironment.Resource("classdata.tpk"));return am;}
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static void Rewrite(string path,Edit[] edits)
    {
        var am=Manager();var temp=path+".unpacked";var packed=path+".packed";
        var preserved=new Dictionary<string,string>();var directoryNames=new List<string>();var rawEntries=new Dictionary<int,string>();
        try
        {
            var bundle=am.LoadBundleFile(path,true);
            for(var i=0;i<bundle.file.BlockAndDirInfo.DirectoryInfos.Count;i++)
            {
                directoryNames.Add(bundle.file.BlockAndDirInfo.DirectoryInfos[i].Name);
                if(!bundle.file.IsAssetsFile(i)){bundle.file.GetFileRange(i,out var offset,out var size);bundle.file.DataReader.Position=offset;rawEntries[i]=Hash(bundle.file.DataReader.ReadBytes(checked((int)size)));continue;}
                var af=am.LoadAssetsFileFromBundle(bundle,i,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                foreach(var info in af.file.Metadata.AssetInfos)
                {
                    var edit=edits.SingleOrDefault(e=>e.Asset.FileIndex==i&&e.Asset.PathId==info.PathId);
                    if(edit is null){af.file.Reader.Position=info.GetAbsoluteByteOffset(af.file);preserved[$"{i}:{info.PathId}"]=Hash(af.file.Reader.ReadBytes(checked((int)info.ByteSize)));continue;}
                    var field=am.GetBaseField(af,info);
                    if(field["m_Name"].AsString!=edit.Asset.Name)throw new InvalidDataException("原资源名称不匹配");
                    if(edit.Png is not null)
                    {
                        if(info.TypeId!=(int)AssetClassID.Texture2D)throw new InvalidDataException("目标不是Texture2D");
                        using var image=Image.Load<Rgba32>(edit.Png);image.Mutate(c=>c.Flip(FlipMode.Vertical));var bytes=new byte[image.Width*image.Height*4];image.CopyPixelDataTo(bytes);
                        var tex=TextureFile.ReadTextureFile(field);tex.m_TextureFormat=(int)TextureFormat.RGBA32;tex.m_Width=image.Width;tex.m_Height=image.Height;tex.m_MipCount=1;tex.SetPictureData(bytes,image.Width,image.Height);tex.WriteTo(field);
                        field["m_TextureFormat"].AsInt=(int)TextureFormat.RGBA32;
                        if(!field["m_CompleteImageSize"].IsDummy)field["m_CompleteImageSize"].AsInt=bytes.Length;
                        if(!field["m_MipCount"].IsDummy)field["m_MipCount"].AsInt=1;
                        if(!field["m_StreamData"].IsDummy){field["m_StreamData"]["offset"].AsULong=0;field["m_StreamData"]["size"].AsUInt=0;field["m_StreamData"]["path"].AsString="";}
                    }
                    else{if(info.TypeId!=(int)AssetClassID.TextAsset)throw new InvalidDataException("目标不是TextAsset");try{field["m_Script"].AsByteArray=edit.Text!;}catch{field["m_Script"]["Array"].AsByteArray=edit.Text!;}}
                    info.SetNewData(field);
                }
                if(edits.Any(e=>e.Asset.FileIndex==i))bundle.file.BlockAndDirInfo.DirectoryInfos[i].SetNewData(af.file);
            }
            using(var writer=new AssetsFileWriter(temp))bundle.file.Write(writer);
            am.UnloadAll();am=Manager();var repack=am.LoadBundleFile(temp);using(var writer=new AssetsFileWriter(packed))repack.file.Pack(writer,AssetBundleCompressionType.LZ4);
            am.UnloadAll();am=Manager();var verify=am.LoadBundleFile(packed,true);var verified=0;var untouched=0;
            if(!verify.file.BlockAndDirInfo.DirectoryInfos.Select(d=>d.Name).SequenceEqual(directoryNames))throw new InvalidDataException("Bundle目录引用发生变化");
            for(var i=0;i<verify.file.BlockAndDirInfo.DirectoryInfos.Count;i++)
            {
                if(!verify.file.IsAssetsFile(i)){verify.file.GetFileRange(i,out var offset,out var size);verify.file.DataReader.Position=offset;if(rawEntries[i]!=Hash(verify.file.DataReader.ReadBytes(checked((int)size))))throw new InvalidDataException("非目标资源数据发生变化");continue;}var af=am.LoadAssetsFileFromBundle(verify,i,false);am.LoadClassDatabaseFromPackage(af.file.Metadata.UnityVersion);
                foreach(var info in af.file.Metadata.AssetInfos)
                {
                    var edit=edits.SingleOrDefault(e=>e.Asset.FileIndex==i&&e.Asset.PathId==info.PathId);
                    if(edit is null){af.file.Reader.Position=info.GetAbsoluteByteOffset(af.file);if(preserved[$"{i}:{info.PathId}"]!=Hash(af.file.Reader.ReadBytes(checked((int)info.ByteSize))))throw new InvalidDataException("未修改对象字节发生变化");untouched++;continue;}
                    var field=am.GetBaseField(af,info);if(field["m_Name"].AsString!=edit.Asset.Name)throw new InvalidDataException("写出资源名称不一致");
                    if(edit.Png is not null){var tex=TextureFile.ReadTextureFile(field);tex.FillPictureData(af);var bytes=tex.DecodeTextureRaw(tex.pictureData)!;using var expected=Image.Load<Bgra32>(edit.Png);expected.Mutate(c=>c.Flip(FlipMode.Vertical));var pixels=new byte[expected.Width*expected.Height*4];expected.CopyPixelDataTo(pixels);if(tex.m_Width!=expected.Width||tex.m_Height!=expected.Height||!pixels.SequenceEqual(bytes))throw new InvalidDataException("Texture2D回读像素不一致");}
                    else{byte[] bytes;try{bytes=field["m_Script"].AsByteArray;}catch{bytes=field["m_Script"]["Array"].AsByteArray;}if(!bytes.SequenceEqual(edit.Text!))throw new InvalidDataException($"文本资源「{edit.Asset.Name}」回读不一致");}verified++;
                }
            }
            if(verified!=edits.Length||untouched!=preserved.Count)throw new InvalidDataException("资源数量或PathID发生变化");
            am.UnloadAll();File.Copy(packed,path,true);
        }
        finally{am.UnloadAll();if(File.Exists(temp))File.Delete(temp);if(File.Exists(packed))File.Delete(packed);}
    }
}
