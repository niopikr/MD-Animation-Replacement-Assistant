using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using CardAssetWorkbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;
public static class PortableSelfTest
{
    public static async Task Run(string root,string output)
    {
        Directory.CreateDirectory(output);var tests=new List<object>();void Check(string name,bool value){tests.Add(new{Name=name,Passed=value});if(!value)throw new Exception(name);}
        var lib=AppEnvironment.Library(root);var store=new ProjectStore(root,Path.Combine(output,"projects"));
        Check("自带.NET运行时不依赖外部SDK",Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()).TrimEnd('\\','/').Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase));
        Check("目录有518张卡且515张可编辑",JsonFile.Read<LibraryState>(Path.Combine(lib,"library-state.json")).Cards.Count==518&&store.Choices().Count==515);
        Check("目录不携带PNG或原始Bundle",!Directory.GetFiles(lib,"*.png",SearchOption.AllDirectories).Any()&&!Directory.GetDirectories(lib,"游戏关联文件",SearchOption.AllDirectories).Any());
        Check("首次读取目录没有自动扫描",!File.Exists(Path.Combine(AppEnvironment.Runtime(root),"素材更新.log")));
        using var sam=new SamService(store);Check("SAM来自便携包models目录",sam.ModelDirectory==Path.Combine(root,"models","sam2"));
        var p=store.Create(new(3801,""));var project=store.DirectoryFor(p.Id);var part=p.Parts[0];
        Check("选卡时才提取对应模型和贴图",p.SourceVersion.StartsWith(OnDemandLibrary.CacheRoot(root),StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(project,"templates",part.Key,"reference.png")));
        var cachePath=p.SourceVersion;var again=store.Create(new(3801,p.Variant));Check("同卡同资源复用临时素材",again.SourceVersion==cachePath);
        using(var stream=File.OpenRead(Path.Combine(project,"templates",part.Key,"reference.png")))store.SetArtwork(p,stream);
        using var image=Image.Load<Rgba32>(Path.Combine(project,"artwork.png"));var box=ProjectStore.VisibleBounds(image);
        var prediction=await sam.Predict(p,new("",[[box.X+box.Width/2f,box.Y+box.Height/2f,1]],false));using var result=Image.Load<Rgba32>(prediction);
        Check("便携包实际SAM2.1推理输出尺寸正确",result.Size==image.Size);
        var clear=JsonSerializer.SerializeToNode(OnDemandLibrary.ClearUnused(store))!;Check("清理缓存保留已有项目引用",Directory.Exists(cachePath));
        var qa=Path.Combine(output,"update-root");Directory.CreateDirectory(qa);JsonFile.Write(Path.Combine(qa,"portable.json"),new{Format="md-animation-workbench-portable-v1",SeedAccount=new InstallSettings(root).Read().Account});
        var qlib=AppEnvironment.Library(qa);Directory.CreateDirectory(qlib);var state=JsonFile.Read<LibraryState>(Path.Combine(lib,"library-state.json"));state.Cards=state.Cards.Where(c=>c.Key==3801).ToDictionary(c=>c.Key,c=>c.Value);
        var card=state.Cards[3801];foreach(var file in Directory.GetFiles(Path.Combine(lib,card.VersionFolder))){var dest=Path.Combine(qlib,card.VersionFolder,Path.GetFileName(file));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest);}
        JsonFile.Write(Path.Combine(qlib,"library-state.json"),state);File.Copy(Path.Combine(lib,"catalog-only.json"),Path.Combine(qlib,"catalog-only.json"));File.Copy(Path.Combine(lib,"source-hashes.json"),Path.Combine(qlib,"source-hashes.json"));
        var cache=Path.Combine(qa,"卡片素材工作区","索引");Directory.CreateDirectory(cache);File.Copy(Path.Combine(root,"卡片素材工作区","索引","index.json"),Path.Combine(cache,"index.json"));
        var settings=new InstallSettings(qa);var actual=new InstallSettings(root).Read();settings.Save(actual);
        var invalid=false;try{settings.Save(new GameSettings(output,""));}catch(InvalidDataException){invalid=true;}Check("无效游戏目录不会覆盖有效设置",invalid&&settings.Read()==actual);
        var mapped=AppEnvironment.MapGamePath(qa,@"Z:\old-machine\LocalData\deadbeef\0000\12\12345678");Check("旧机器游戏路径映射至所选目录账号",mapped==Path.Combine(actual.GameRoot,"LocalData",actual.Account,"0000","12","12345678"));
        var qstore=new ProjectStore(qa);_ = qstore.Choices();var updater=new LibraryUpdater(qstore,settings,[3801]);updater.Start();var deadline=DateTime.UtcNow.AddMinutes(4);while(updater.Running&&DateTime.UtcNow<deadline)await Task.Delay(500);
        var status=JsonSerializer.SerializeToNode(updater.Snapshot())!;Check("真实增量扫描和目录更新完成",status["Phase"]!.GetValue<string>()=="completed");
        Check("增量更新目录不生成全库贴图",!Directory.GetFiles(qlib,"*.png",SearchOption.AllDirectories).Any()&&!Directory.GetDirectories(qlib,"游戏关联文件",SearchOption.AllDirectories).Any());
        Check("更新后目录索引完整生成",File.Exists(Path.Combine(qlib,"索引目录.html"))&&File.Exists(Path.Combine(qlib,"索引目录.csv"))&&qstore.Choices().Count==1);
        var empty=JsonFile.Read<LibraryState>(Path.Combine(qlib,"library-state.json"));empty.Cards.Clear();JsonFile.Write(Path.Combine(qlib,"library-state.json"),empty);
        var discovered=PortableCatalog.Update(AssetIndex.Load(Path.Combine(cache,"index.json")),qlib,_=>{},CancellationToken.None,[3801]);qstore.InvalidateChoices();
        Check("新发现动画可仅生成目录元数据",discovered.Added==1&&discovered.Failed==0&&!Directory.GetFiles(qlib,"*.png",SearchOption.AllDirectories).Any());
        var newProject=qstore.Create(new(3801,""));Check("新增目录也支持按需提取",newProject.Parts.Count==p.Parts.Count&&File.Exists(Path.Combine(qstore.DirectoryFor(newProject.Id),"source",newProject.AtlasFile)));
        foreach(var item in p.Parts)store.Confirm(p,item,null,empty:item.Number==p.Parts[0].Number);
        var exported=JsonSerializer.SerializeToNode(new VariantExportCoordinator(store,new Exporter(store)).Export(p))!;
        var exportedFolder=exported["Folder"]!.GetValue<string>();
        Check("按需素材可导出同名游戏文件及备份",File.Exists(Path.Combine(exportedFolder,"替换文件完成标记.txt"))&&Directory.GetFiles(Path.Combine(exportedFolder,"原文件备份"),"*",SearchOption.AllDirectories).Length>0);
        Check("独立版导出不再生成外部预览启动器",!Directory.GetFiles(exportedFolder,"*EX Studio*",SearchOption.AllDirectories).Any());
        Check("导出总文件夹按卡片ID和游戏名命名",Path.GetFileName(exportedFolder)==$"{p.CardId}-{p.ChineseName}");
        Check("替换文件只含0000一级目录",Directory.GetDirectories(Path.Combine(exportedFolder,"替换文件")).Select(Path.GetFileName).SequenceEqual(new[]{"0000"}));
        var packageReport=JsonNode.Parse(File.ReadAllText(Path.Combine(exportedFolder,"替换清单.json")))!;
        Check("清单含0000相对路径且仍定位实际账号目标",packageReport["Files"]!.AsArray().All(f=>f!["File"]!.GetValue<string>().Replace('\\','/').StartsWith("0000/")&&File.Exists(f["Target"]!.GetValue<string>())&&ProjectStore.Hash(Path.Combine(exportedFolder,f["Replacement"]!.GetValue<string>()))==f["ReplacementSha256"]!.GetValue<string>()));
        JsonFile.Write(Path.Combine(output,"selftest-result.json"),new{Passed=tests.Count,Tests=tests,Project=p.Id,Runtime=RuntimeEnvironment.GetRuntimeDirectory(),Update=status});
        Console.WriteLine("独立版检查通过 "+tests.Count+" 项。");
    }
}
