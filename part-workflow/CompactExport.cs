using System.Text.Json;

namespace PartWorkflow;
public record CompactState(string Internal,string Public,int Changes,int BundleCount,int CompanionCount,bool Ready=false);
public record CompactResult(string Folder,string Relative,int Changes,int BundleCount,int CompanionCount,bool Ready);
public sealed class CompactExport(ProjectStore store,Exporter exporter)
{
    CompactResult Result(ProjectState p,CompactState s)=>new(ProjectStore.Under(store.DirectoryFor(p.Id),s.Public),s.Internal,s.Changes,s.BundleCount,s.CompanionCount,s.Ready);
    public CompactResult Export(ProjectState p)
    {
        var output=JsonSerializer.SerializeToNode(p.IsEffect ? EffectProjects.Export(store, exporter, p) :
            new VariantExportCoordinator(store,exporter).Export(p))!;
        var rel=output["Relative"]!.GetValue<string>();
        if(!rel.Replace('\\','/').StartsWith("内部导出/",StringComparison.Ordinal))throw new InvalidDataException("内部导出路径无效");
        var state=new CompactState(rel,"导出/"+rel.Replace('\\','/')["内部导出/".Length..],output["Changes"]!.GetValue<int>(),output["BundleCount"]!.GetValue<int>(),output["CompanionCount"]!.GetValue<int>());
        var root=store.DirectoryFor(p.Id);
        var internalRoot=ProjectStore.Under(root,rel);
        if(!File.Exists(Path.Combine(internalRoot,"跨画质导出完成标记.txt")))throw new InvalidDataException("游戏文件尚未生成完整");
        var target=ProjectStore.Under(root,state.Public);
        var temporary=ProjectStore.Under(root,rel+"/精简包准备-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            var source=Path.Combine(internalRoot,"替换文件");
            foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {
                var relative=Path.GetRelativePath(source,file);
                if(!relative.Replace('\\','/').StartsWith("0000/",StringComparison.Ordinal))throw new InvalidDataException("输出必须从0000开始");
                var dest=ProjectStore.Under(temporary,Path.Combine("替换",relative));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file,dest);
            }
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
        if(!state.Ready||!Directory.Exists(Path.Combine(folder,"替换","0000")))throw new InvalidOperationException("替换文件尚未生成，请重新导出");
        return folder;
    }
}
