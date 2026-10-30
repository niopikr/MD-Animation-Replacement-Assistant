using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartWorkflow;

// A high quality editing source does not imply the game reads high quality cut-in assets.
public sealed class VariantExportCoordinator(ProjectStore store,Exporter preview)
{
    public object Export(ProjectState source)
    {
        var primary=JsonSerializer.SerializeToNode(new GameReplacementExporter(store,preview).Export(source,compatible:true))!;
        var folder=primary["Folder"]!.GetValue<string>();var reportPath=Path.Combine(folder,"替换清单.json");
        var report=JsonNode.Parse(File.ReadAllText(reportPath))!;var files=report["Files"]!.AsArray();
        var models=new List<object>();var notes=new List<string>();
        try
        {
            var selectedLabel=Path.GetFileName(Path.GetDirectoryName(source.Variant))!;
            var region=selectedLabel.Split('-')[0];
            var sourceManifest=JsonNode.Parse(File.ReadAllText(Path.Combine(source.SourceVersion,"manifest.json")))!;
            var companions=sourceManifest["Preview"]!["Configurations"]!.AsArray().Select(c=>c!.GetValue<string>())
                .Where(v=>v!=source.Variant&&Path.GetFileName(Path.GetDirectoryName(v))!.StartsWith(region+"-低清-",StringComparison.Ordinal)).ToArray();
            foreach(var variant in companions)
            {
                var label=Path.GetFileName(Path.GetDirectoryName(variant))!;
                var childStore=new ProjectStore(store.Workspace,Path.Combine(folder,"辅助构建",label));
                var child=childStore.Create(new(source.CardId,variant));
                if(child.SourceVersion!=source.SourceVersion)
                {
                    string[] Sources(string v)=>JsonNode.Parse(File.ReadAllText(Path.Combine(v,"manifest.json")))!["OriginalFiles"]!.AsArray().Select(a=>a!["BundleId"]!.GetValue<string>()+":"+a["Sha256"]!.GetValue<string>()).Order().ToArray();
                    if(!Sources(child.SourceVersion).SequenceEqual(Sources(source.SourceVersion)))throw new InvalidDataException("游戏资源已更新，请在新版本重建项目后导出");
                }
                var childRoot=childStore.DirectoryFor(child.Id);var sourceRoot=store.DirectoryFor(source.Id);
                // Recognize only this source project's recorded previous installs, including prior SD outputs.
                foreach(var old in source.Exports)
                {
                    var oldReport=ProjectStore.Under(sourceRoot,old+"/替换清单.json");if(!File.Exists(oldReport))continue;
                    var relative="known/"+child.Exports.Count;var target=ProjectStore.Under(childRoot,relative+"/替换清单.json");Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(oldReport,target);child.Exports.Add(relative);
                }
                foreach(var targetPart in child.Parts)
                {
                    var original=source.Parts.SingleOrDefault(p=>p.Slot==targetPart.Slot&&p.Name==targetPart.Name);
                    if(original is null){childStore.Confirm(child,targetPart,null);continue;}
                    if(original.Status=="original"){childStore.Confirm(child,targetPart,null);continue;}
                    if(original.Uvs.Length!=targetPart.Uvs.Length||!original.Uvs.Zip(targetPart.Uvs).All(pair=>Math.Abs(pair.First-pair.Second)<.0001))
                        throw new InvalidDataException("不同画质部件UV不一致，无法直接同步："+original.Name);
                    if(original.Status=="empty"){childStore.Confirm(child,targetPart,null,empty:true);continue;}
                    var revision=JsonFile.Read<Revision>(ProjectStore.Under(sourceRoot,original.ConfirmedRevision));
                    using var image=Image.Load<Rgba32>(ProjectStore.Under(sourceRoot,revision.Aligned));
                    image.Mutate(c=>c.Resize(targetPart.Width,targetPart.Height));
                    var relative="synced/"+targetPart.Key;var dst=ProjectStore.Under(childRoot,relative);Directory.CreateDirectory(dst);
                    using var mask=new Image<Rgba32>(targetPart.Width,targetPart.Height,new Rgba32(255,255,255,255));
                    JsonFile.Png(Path.Combine(dst,"mask.png"),mask);JsonFile.Png(Path.Combine(dst,"cutout.png"),image);
                    targetPart.DraftMask=relative+"/mask.png";targetPart.DraftCutout=relative+"/cutout.png";
                    using var bytes=new MemoryStream();image.SaveAsPng(bytes);
                    var placement=new Placement{X=targetPart.Width/2d,Y=targetPart.Height/2d,Width=targetPart.Width,Height=targetPart.Height};
                    childStore.Confirm(child,targetPart,new(Convert.ToBase64String(bytes.ToArray()),placement));
                }
                foreach(var changed in source.Parts.Where(p=>p.Status is "replaced" or "empty"))
                    if(!child.Parts.Any(p=>p.Name==changed.Name&&p.Slot==changed.Slot))throw new InvalidDataException("低清版本没有对应部件："+changed.Name);
                var childResult=JsonSerializer.SerializeToNode(new GameReplacementExporter(childStore,new Exporter(childStore)).Export(child,compatible:true))!;
                var childFolder=childResult["Folder"]!.GetValue<string>();
                var childReport=JsonNode.Parse(File.ReadAllText(Path.Combine(childFolder,"替换清单.json")))!;
                foreach(var category in new[]{"替换文件","原文件备份","替换前文件备份"})
                {
                    var basePath=Path.Combine(childFolder,category);if(!Directory.Exists(basePath))continue;
                    foreach(var file in Directory.GetFiles(basePath,"*",SearchOption.AllDirectories))
                    {
                        var dest=ProjectStore.Under(folder,Path.Combine(category,Path.GetRelativePath(basePath,file)));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if(File.Exists(dest)){if(ProjectStore.Hash(dest)!=ProjectStore.Hash(file))throw new InvalidDataException("不同画质导出共享同名Bundle且内容冲突");}
                        else File.Copy(file,dest);
                    }
                }
                foreach(var file in childReport["Files"]!.AsArray())if(!files.Any(f=>f!["File"]!.GetValue<string>()==file!["File"]!.GetValue<string>()))files.Add(file!.DeepClone());
                var relativeModel="其他画质/"+label+"/游戏图集核对";var model=ProjectStore.Under(folder,relativeModel);Directory.CreateDirectory(model);
                foreach(var file in Directory.GetFiles(Path.Combine(childFolder,"游戏图集核对")))File.Copy(file,Path.Combine(model,Path.GetFileName(file)));
                File.Copy(Path.Combine(childFolder,"原格式兼容验证.json"),Path.Combine(Path.GetDirectoryName(model)!,"原格式兼容验证.json"));
                models.Add(new{Variant=variant,Label=label,Model=relativeModel,child.SkeletonFile,child.AtlasFile});notes.Add(label);
            }
            report["CompanionVariants"]=JsonSerializer.SerializeToNode(notes);File.WriteAllText(reportPath,report.ToJsonString(JsonFile.Options));
            JsonFile.Write(Path.Combine(folder,"跨画质导出.json"),new{PrimaryVariant=source.Variant,Models=models,Verified=true});
            if(notes.Count>0)
            {
                var text="\r\n本包已同步包含低清演出资源："+string.Join("、",notes)+"。请把替换文件目录内全部文件一起覆盖，不要仅挑高清文件。原有确认进度没有改变。\r\n";
                File.AppendAllText(Path.Combine(folder,"替换说明.txt"),text);File.AppendAllText(Path.Combine(folder,"先看这里.txt"),text);
            }
            File.WriteAllText(Path.Combine(folder,"跨画质导出完成标记.txt"),"主版本和低清版本均已生成并验证。\r\n");
            return new{Folder=folder,Relative=primary["Relative"]!.GetValue<string>(),Config=primary["Config"]!.GetValue<string>(),Changes=primary["Changes"]!.GetValue<int>(),BundleCount=files.Count,CompanionCount=notes.Count,Companions=notes,IsFinal=true};
        }
        catch(Exception ex){File.Delete(Path.Combine(folder,"替换文件完成标记.txt"));File.WriteAllText(Path.Combine(folder,"跨画质导出未完成.txt"),ex.Message);throw;}
    }
}
