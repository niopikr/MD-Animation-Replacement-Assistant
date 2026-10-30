using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;
public static class CompatSelfTest
{
    public static void Run(string workspace,string output)
    {
        Directory.CreateDirectory(output);var tests=new List<object>();
        void Check(string name,bool pass){tests.Add(new{Name=name,Passed=pass});if(!pass)throw new Exception(name);}
        var store=new ProjectStore(workspace,Path.Combine(output,"projects"));var p=store.Create(new(21460,""));var root=store.DirectoryFor(p.Id);
        var production=Path.Combine(workspace,"卡片素材工作区","部件工作流项目");
        // Read only prior generated install manifests; no user artwork is loaded.
        var knownFiles=Directory.GetFiles(production,"替换清单.json",SearchOption.AllDirectories).SelectMany(f=>JsonNode.Parse(File.ReadAllText(f))!["Files"]!.AsArray()).Select(f=>f!.DeepClone()).ToArray();
        Directory.CreateDirectory(Path.Combine(root,"known-install"));JsonFile.Write(Path.Combine(root,"known-install","替换清单.json"),new{Files=knownFiles});p.Exports.Add("known-install");
        var before=knownFiles.Select(f=>f["Target"]!.GetValue<string>()).Distinct().ToDictionary(f=>f,ProjectStore.Hash);
        foreach(var part in p.Parts)store.Confirm(p,part,null);
        var head=p.Parts[0];using(var f=File.OpenRead(Path.Combine(root,"templates",head.Key,"reference.png")))store.SetArtwork(p,f);
        using var mask=new Image<Rgba32>(head.Width,head.Height,new Rgba32(255,255,255,255));using var maskBytes=new MemoryStream();mask.SaveAsPng(maskBytes);store.SaveSelection(p,head,Convert.ToBase64String(maskBytes.ToArray()));
        using var green=new Image<Rgba32>(head.Width,head.Height,new Rgba32(0,230,20,255));using var greenBytes=new MemoryStream();green.SaveAsPng(greenBytes);store.Confirm(p,head,new(Convert.ToBase64String(greenBytes.ToArray()),head.Placement));
        store.Confirm(p,p.Parts.Single(x=>x.Name=="Body"),null,empty:true);
        var exporter=new GameReplacementExporter(store,new Exporter(store));var result=JsonSerializer.SerializeToNode(exporter.Export(p,compatible:true))!;
        var folder=result["Folder"]!.GetValue<string>();var report=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"原格式兼容验证.json")))!;
        Check("原Atlas字节完全不变",report["AtlasByteIdentical"]!.GetValue<bool>());
        Check("两个修改贴图Bundle和原AtlasBundle都导出",result["BundleCount"]!.GetValue<int>()==3);
        Check("每个CAB元数据及原resS存储保持一致",report["Checks"]!.AsArray().All(c=>c!["AllCabMetadataIdentical"]!.GetValue<bool>()&&c["OriginalStreamLayout"]!.GetValue<bool>()));
        var textures=report["Checks"]!.AsArray().SelectMany(c=>c!["Textures"]!.AsArray()).ToArray();
        Check("全部保持2048方形BC7单Mip原格式",textures.Length==2&&textures.All(t=>t!["Width"]!.GetValue<int>()==2048&&t["Height"]!.GetValue<int>()==2048&&t["Format"]!.GetValue<string>()=="BC7"&&t["MipCount"]!.GetValue<int>()==1&&t["StreamSize"]!.GetValue<uint>()==4194304));
        Check("仅相关BC7块重编码其他压缩块原样保留",textures.All(t=>t!["ChangedBlocks"]!.GetValue<int>()>0&&t["OriginalBlocks"]!.GetValue<int>()>0));
        Check("实际游戏文件未被改写",before.All(x=>ProjectStore.Hash(x.Key)==x.Value));
        var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"替换清单.json")))!;
        Check("识别旧版已安装包并保留原始备份",manifest["CompatibleOriginalFormat"]!.GetValue<bool>()&&manifest["Files"]!.AsArray().All(f=>ProjectStore.Hash(Path.Combine(folder,f!["Backup"]!.GetValue<string>()))==f["OriginalSha256"]!.GetValue<string>()));
        var model=Path.Combine(folder,"游戏图集核对");
        var body=p.Parts.Single(x=>x.Name=="Body");using var bodyPage=Image.Load<Rgba32>(Path.Combine(model,body.Page));using var bodyImage=Atlas.Extract(bodyPage,body);using var footprint=Atlas.MeshMask(body);
        var total=0;var clear=0;for(var y=8;y<body.Height-8;y++)for(var x=8;x<body.Width-8;x++){if(footprint[x,y].PackedValue==0)continue;total++;if(bodyImage[x,y].A<=3)clear++;}
        Check("空素材在实际BC7解码图中保持透明",total>100&&clear>(int)(total*.95));
        using var headPage=Image.Load<Rgba32>(Path.Combine(model,head.Page));using var headImage=Atlas.Extract(headPage,head);using var hm=Atlas.MeshMask(head);var colored=0;
        for(var y=0;y<head.Height;y++)for(var x=0;x<head.Width;x++)if(hm[x,y].PackedValue!=0&&headImage[x,y].G>180&&headImage[x,y].R<50)colored++;
        Check("彩色替换在原UV位置可从BC7贴图读回",colored>100);
        JsonFile.Write(Path.Combine(output,"selftest-result.json"),new{Passed=tests.Count,Tests=tests,Project=p.Id,Export=result});Console.WriteLine($"兼容检查通过 {tests.Count} 项：{folder}");
    }
}
