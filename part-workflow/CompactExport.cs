using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;
public record CompactState(string Internal,string Public,int Changes,int BundleCount,int CompanionCount,bool Ready=false);
public record CompactResult(string Folder,string Relative,int Changes,int BundleCount,int CompanionCount,bool Ready);
public record FinalizeExportRequest(string Revision,string Image);
public sealed class CompactExport(ProjectStore store,Exporter exporter)
{
    CompactResult Result(ProjectState p,CompactState s)=>new(ProjectStore.Under(store.DirectoryFor(p.Id),s.Public),s.Internal,s.Changes,s.BundleCount,s.CompanionCount,s.Ready);
    public CompactResult Begin(ProjectState p)
    {
        var output=JsonSerializer.SerializeToNode(new VariantExportCoordinator(store,exporter).Export(p))!;
        var rel=output["Relative"]!.GetValue<string>();
        if(!rel.Replace('\\','/').StartsWith("内部导出/",StringComparison.Ordinal))throw new InvalidDataException("内部导出路径无效");
        var state=new CompactState(rel,"导出/"+rel.Replace('\\','/')["内部导出/".Length..],output["Changes"]!.GetValue<int>(),output["BundleCount"]!.GetValue<int>(),output["CompanionCount"]!.GetValue<int>());
        JsonFile.Write(ProjectStore.Under(store.DirectoryFor(p.Id),rel+"/精简导出.json"),state);
        return Result(p,state);
    }
    public CompactResult Complete(ProjectState p,FinalizeExportRequest request)
    {
        if(!p.Exports.Contains(request.Revision))throw new InvalidDataException("导出不属于本项目");
        var root=store.DirectoryFor(p.Id);var internalRoot=ProjectStore.Under(root,request.Revision);
        var state=JsonFile.Read<CompactState>(Path.Combine(internalRoot,"精简导出.json"));
        if(state.Internal!=request.Revision||!File.Exists(Path.Combine(internalRoot,"跨画质导出完成标记.txt")))throw new InvalidDataException("游戏文件尚未生成完整");
        using var frame=ProjectStore.DecodePng(request.Image);
        if(frame.Width!=1024||frame.Height!=768)throw new InvalidDataException("尾帧图必须是1024×768");
        var target=ProjectStore.Under(root,state.Public);
        if(Directory.Exists(target))return Result(p,state with{Ready=true});
        var temporary=ProjectStore.Under(root,request.Revision+"/精简包准备-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            foreach(var (from,to) in new[]{("替换文件","替换"),("原文件备份","原始")})
            {
                var source=Path.Combine(internalRoot,from);
                foreach(var f in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
                {
                    var relative=Path.GetRelativePath(source,f);
                    if(!relative.Replace('\\','/').StartsWith("0000/",StringComparison.Ordinal))throw new InvalidDataException("输出必须从0000开始");
                    var dest=ProjectStore.Under(temporary,Path.Combine(to,relative));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(f,dest);
                    if(ProjectStore.Hash(dest)!=ProjectStore.Hash(f))throw new IOException("精简文件复制验证失败");
                }
            }
            JsonFile.Png(Path.Combine(temporary,"替换后尾帧.png"),frame);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(temporary,target);
            state=state with{Ready=true};JsonFile.Write(Path.Combine(internalRoot,"精简导出.json"),state);
            return Result(p,state);
        }
        finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    public static string OpenFolder(ProjectStore store,ProjectState p,string relative)
    {
        if(!p.Exports.Contains(relative))throw new InvalidDataException("目录不是本项目导出");
        var internalRoot=ProjectStore.Under(store.DirectoryFor(p.Id),relative);var file=Path.Combine(internalRoot,"精简导出.json");
        if(!File.Exists(file))return internalRoot;
        var state=JsonFile.Read<CompactState>(file);var folder=ProjectStore.Under(store.DirectoryFor(p.Id),state.Public);
        if(!state.Ready||!File.Exists(Path.Combine(folder,"替换后尾帧.png")))throw new InvalidOperationException("尾帧图尚未生成，请重新导出");
        return folder;
    }
}
