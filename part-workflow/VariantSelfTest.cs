using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
namespace PartWorkflow;
public static class VariantSelfTest
{
    public static void Run(string workspace,string output)
    {
        Directory.CreateDirectory(output);var tests=new List<object>();
        void Check(string name,bool pass){tests.Add(new{Name=name,Passed=pass});if(!pass)throw new Exception(name);}
        var store=new ProjectStore(workspace,Path.Combine(output,"projects"));var p=store.Create(new(16386,""));var root=store.DirectoryFor(p.Id);
        var known=Directory.GetFiles(Path.Combine(workspace,"卡片素材工作区","部件工作流项目"),"替换清单.json",SearchOption.AllDirectories).SelectMany(f=>JsonNode.Parse(File.ReadAllText(f))!["Files"]!.AsArray()).Select(f=>f!.DeepClone()).ToArray();
        Directory.CreateDirectory(Path.Combine(root,"known"));JsonFile.Write(Path.Combine(root,"known","替换清单.json"),new{Files=known});p.Exports.Add("known");
        var before=known.Select(f=>f["Target"]!.GetValue<string>()).Distinct().ToDictionary(f=>f,ProjectStore.Hash);
        foreach(var part in p.Parts)store.Confirm(p,part,null);
        var body=p.Parts.Single(x=>x.Name=="Body");using(var f=File.OpenRead(Path.Combine(root,"templates",body.Key,"reference.png")))store.SetArtwork(p,f);
        using var mask=new Image<Rgba32>(body.Width,body.Height,new Rgba32(255,255,255,255));using var maskBytes=new MemoryStream();mask.SaveAsPng(maskBytes);store.SaveSelection(p,body,Convert.ToBase64String(maskBytes.ToArray()));
        using var color=new Image<Rgba32>(body.Width,body.Height,new Rgba32(0,220,30,255));using var bytes=new MemoryStream();color.SaveAsPng(bytes);store.Confirm(p,body,new(Convert.ToBase64String(bytes.ToArray()),body.Placement));
        var empty=p.Parts.First(x=>x.Page==body.Page&&x.Name!=body.Name);store.Confirm(p,empty,null,empty:true);
        var revisionIds=p.Parts.Select(x=>x.ConfirmedRevision).ToArray();
        var result=JsonSerializer.SerializeToNode(new VariantExportCoordinator(store,new Exporter(store)).Export(p))!;
        var folder=result["Folder"]!.GetValue<string>();var report=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"替换清单.json")))!;
        Check("高清项目一次导出同时含1组低清演出",result["CompanionCount"]!.GetValue<int>()==1);
        Check("鲜花高清低清贴图及Atlas共4个Bundle",result["BundleCount"]!.GetValue<int>()==4);
        var fileIds=report["Files"]!.AsArray().Select(f=>Path.GetFileName(f!["Target"]!.GetValue<string>())).ToHashSet();
        Check("包含实测游戏读取的376af1d5与7a54f4f4",fileIds.Contains("376af1d5")&&fileIds.Contains("7a54f4f4"));
        Check("原高清确认步骤和历史版本保持不变",store.Load(p.Id).Parts.Select(x=>x.ConfirmedRevision).SequenceEqual(revisionIds));
        Check("原游戏文件所有哈希均不变",before.All(f=>ProjectStore.Hash(f.Key)==f.Value));
        Check("合并后的全部输出及原始备份哈希正确",report["Files"]!.AsArray().All(f=>ProjectStore.Hash(Path.Combine(folder,f!["Replacement"]!.GetValue<string>()))==f["ReplacementSha256"]!.GetValue<string>()&&ProjectStore.Hash(Path.Combine(folder,f["Backup"]!.GetValue<string>()))==f["OriginalSha256"]!.GetValue<string>()));
        var batch=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"跨画质导出.json")))!;var model=batch["Models"]![0]!;
        var childRoot=Directory.GetFiles(Path.Combine(folder,"辅助构建"),"project.json",SearchOption.AllDirectories).Single();var child=JsonFile.Read<ProjectState>(childRoot);
        Check("高清到低清全部部件名称和UV可对应",child.Parts.Count==p.Parts.Count&&child.Parts.All(t=>p.Parts.Any(s=>s.Name==t.Name&&s.Slot==t.Slot&&s.Uvs.SequenceEqual(t.Uvs))));
        Check("空素材确认同步到低清且其他原图保留",child.Parts.Single(x=>x.Name==empty.Name).Status=="empty"&&child.Parts.Count(x=>x.Status=="replaced")==1);
        var lowModel=Path.Combine(folder,model["Model"]!.GetValue<string>());
        Check("低清Atlas字节与原始资源一致",ProjectStore.Hash(Path.Combine(lowModel,child.AtlasFile))==child.SourceHashes[child.AtlasFile]);
        var lowBody=child.Parts.Single(x=>x.Name=="Body");using var page=Image.Load<Rgba32>(Path.Combine(lowModel,lowBody.Page));using var actual=Atlas.Extract(page,lowBody);using var footprint=Atlas.MeshMask(lowBody);var greens=0;
        for(var y=0;y<actual.Height;y++)for(var x=0;x<actual.Width;x++)if(footprint[x,y].PackedValue!=0&&actual[x,y].G>170&&actual[x,y].R<60)greens++;
        Check("实际低清BC7图在原Body网格位置含替换色",greens>100);
        Check("只有跨画质全部成功才生成完成标记",File.Exists(Path.Combine(folder,"跨画质导出完成标记.txt"))&&!File.Exists(Path.Combine(folder,"跨画质导出未完成.txt")));
        JsonFile.Write(Path.Combine(output,"selftest-result.json"),new{Passed=tests.Count,Tests=tests,Project=p.Id,Export=result});Console.WriteLine($"跨画质检查通过 {tests.Count} 项：{folder}");
    }
}
