using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CardAssetWorkbench;

public record PreviewResult(int Groups,List<string> Configurations,List<string> Notes);
public static class LibraryPreview
{
    static string Family(string logical)
    {
        var m=Regex.Match(logical,@"^(.*?/(?:highend_hd|sd))(?:/|$)",RegexOptions.IgnoreCase);
        return m.Success?m.Groups[1].Value.ToLowerInvariant():Path.GetDirectoryName(logical)?.ToLowerInvariant()??logical;
    }
    public static List<string> AtlasPages(string text)=>text.Replace("\r","").Split('\n')
        .Select(l=>l.Trim()).Where(l=>!l.Contains(':')&&l.EndsWith(".png",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static PreviewResult Prepare(string cardRoot,int id,List<LibraryAsset> assets)
    {
        var configs=new List<string>();var notes=new List<string>();
        var skeletons=new List<(LibraryAsset Asset,string Version,double Height)>();
        foreach(var a in assets.Where(a=>a.Asset.Type=="TextAsset"&&a.File.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var d=JsonDocument.Parse(File.ReadAllBytes(BulkLibrary.Inside(cardRoot,a.File)));
                if(!d.RootElement.TryGetProperty("bones",out _)||!d.RootElement.TryGetProperty("animations",out _))continue;
                var skeleton=d.RootElement.GetProperty("skeleton");
                var version=skeleton.TryGetProperty("spine",out var sp)?sp.GetString()??"":"";
                var height=skeleton.TryGetProperty("height",out var h)?h.GetDouble():0;
                skeletons.Add((a,version,height));
            }
            catch(JsonException){notes.Add("未按 Spine JSON 识别："+a.File);}
        }
        foreach(var atlas in assets.Where(a=>a.Asset.Type=="TextAsset"&&a.File.EndsWith(".atlas",StringComparison.OrdinalIgnoreCase)))
        {
            var pages=AtlasPages(File.ReadAllText(BulkLibrary.Inside(cardRoot,atlas.File)));
            if(pages.Count==0){notes.Add("图集中未识别到 PNG 页："+atlas.File);continue;}
            var family=Family(atlas.LogicalPath);
            var candidates=skeletons.Where(s=>Family(s.Asset.LogicalPath)==family).ToList();
            if(candidates.Count!=1){notes.Add($"{atlas.File}：同画质找到 {candidates.Count} 份骨骼，未猜测搭配。");continue;}
            var skeleton=candidates[0];
            var pageAssets=new List<LibraryAsset>();
            foreach(var page in pages)
            {
                var expected=Path.GetDirectoryName(atlas.LogicalPath)?.Replace('\\','/')+"/"+page;
                var matching=assets.Where(a=>a.Asset.Type=="Texture2D"&&a.LogicalPath.Equals(expected,StringComparison.OrdinalIgnoreCase)).ToList();
                if(matching.Count==0)matching=assets.Where(a=>a.Asset.Type=="Texture2D"&&Family(a.LogicalPath)==family&&a.Asset.Name.Equals(Path.GetFileNameWithoutExtension(page),StringComparison.OrdinalIgnoreCase)).ToList();
                if(matching.Count!=1){notes.Add($"{atlas.File}：图集页 {page} 找到 {matching.Count} 份，未生成该组预览。");break;}
                pageAssets.Add(matching[0]);
            }
            if(pageAssets.Count!=pages.Count)continue;
            var tail=Regex.Match(atlas.LogicalPath,$@"/monstercutin/(.*?)/p{id}/(.*)",RegexOptions.IgnoreCase);
            var label=tail.Success?tail.Groups[1].Value+"-"+Path.GetDirectoryName(tail.Groups[2].Value)?.Replace('\\','-').Replace('/','-'):atlas.BundleId;
            label=label.Replace("highend_hd","高清").Replace("sd","低清");
            var relative=Path.Combine("外部预览",BulkLibrary.SafeComponent(label));
            var dir=BulkLibrary.Inside(cardRoot,relative);if(Directory.Exists(dir))dir+="-"+atlas.BundleId;
            Directory.CreateDirectory(dir);
            var jsonName=BulkLibrary.SafeComponent(skeleton.Asset.Asset.Name)+".json";
            var atlasName=BulkLibrary.SafeComponent(atlas.Asset.Name.EndsWith(".atlas",StringComparison.OrdinalIgnoreCase)?atlas.Asset.Name[..^6]:atlas.Asset.Name)+".atlas";
            File.Copy(BulkLibrary.Inside(cardRoot,skeleton.Asset.File),Path.Combine(dir,jsonName));
            File.Copy(BulkLibrary.Inside(cardRoot,atlas.File),Path.Combine(dir,atlasName));
            for(int i=0;i<pages.Count;i++)
            {
                var output=BulkLibrary.Inside(dir,pages[i]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(BulkLibrary.Inside(cardRoot,pageAssets[i].File),output);
                if(AssetIndex.Sha(output)!=pageAssets[i].Sha256)throw new IOException("预览贴图副本校验失败。");
            }
            var parts=skeleton.Version.Split('.');var sdk=parts.Length>=2?parts[0]+"."+parts[1]:"";
            var config=Path.Combine(dir,"预览.config.json");
            AssetIndex.Save(config,new{conf_ver=1,type=9,options=new{scale_factor=skeleton.Height>0?110/skeleton.Height:0.04,tex_type=0,edge_padding=false,spine_sdk=sdk},skeleton=jsonName,
                atlases=new[]{new{atlas=atlasName,tex_names=pages.Select(Path.GetFileNameWithoutExtension).ToArray(),textures=pages}}});
            configs.Add(Path.GetRelativePath(cardRoot,config));
        }
        return new(configs.Count,configs,notes);
    }
}
