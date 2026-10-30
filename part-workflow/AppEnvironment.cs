using System.Text.Json.Nodes;

namespace PartWorkflow;
public static class AppEnvironment
{
    public static void EnsureDefaultData(string root)
    {
        var data=Path.Combine(root,"数据");var zip=Path.Combine(data,"默认目录.zip");
        if(File.Exists(Path.Combine(Library(root),"library-state.json"))||!File.Exists(zip))return;
        System.IO.Compression.ZipFile.ExtractToDirectory(zip,data,false);
    }
    public static bool Portable(string root)=>File.Exists(Path.Combine(root,"portable.json"));
    public static string Resource(string name)=>Path.Combine(AppContext.BaseDirectory,"资源",name);
    public static string Runtime(string root)=>Path.Combine(root,Portable(root)?"运行数据":".dev-tools");
    public static string Library(string root)=>Path.Combine(root,"数据","卡片目录");
    public static string MapGamePath(string root,string archivedPath)
    {
        var settings=new InstallSettings(root);var current=settings.Read();
        if(current.GameRoot.Length==0)throw new InvalidOperationException("请先在首页选择游戏安装目录。");
        var path=archivedPath.Replace('\\','/');var local=path.IndexOf("/LocalData/",StringComparison.OrdinalIgnoreCase);
        string relative;
        if(local>=0)
        {
            var tail=path[(local+11)..];var slash=tail.IndexOf('/');if(slash<0)throw new InvalidDataException("原游戏目录记录无效");
            var account=current.Account.Length>0?current.Account:tail[..slash];
            relative="LocalData/"+account+tail[slash..];
        }
        else
        {
            var streaming=path.IndexOf("/masterduel_Data/StreamingAssets/",StringComparison.OrdinalIgnoreCase);
            if(streaming<0)throw new InvalidDataException("无法映射原资源到所选游戏目录");relative=path[(streaming+1)..];
        }
        return ProjectStore.Under(current.GameRoot,relative);
    }
}
public sealed record GameSettings(string GameRoot="",string Account="");
public sealed class InstallSettings(string root)
{
    readonly object gate=new();
    string FilePath=>Path.Combine(AppEnvironment.Runtime(root),"游戏目录.json");
    public GameSettings Read()
    {
        lock(gate)
        {
            if(File.Exists(FilePath))return JsonFile.Read<GameSettings>(FilePath);
            return new GameSettings();
        }
    }
    public static string[] Accounts(string game)=>Directory.Exists(Path.Combine(game,"LocalData"))?Directory.GetDirectories(Path.Combine(game,"LocalData")).Where(d=>Directory.Exists(Path.Combine(d,"0000"))).Select(d=>Path.GetFileName(d)!).Order().ToArray():[];
    public GameSettings Save(GameSettings value)
    {
        lock(gate)
        {
            var game=Path.GetFullPath(value.GameRoot.Trim().Trim('"'));
            if(!File.Exists(Path.Combine(game,"masterduel.exe")))throw new InvalidDataException("所选目录中没有 masterduel.exe，请选择游戏安装根目录。");
            var accounts=Accounts(game);if(accounts.Length==0)throw new InvalidDataException("该目录没有已下载的账号资源，请先启动游戏完成资源下载。");
            var account=accounts.Contains(value.Account)?value.Account:accounts.Contains(Read().Account)?Read().Account:accounts[0];
            var result=new GameSettings(game,account);JsonFile.Write(FilePath,result);return result;
        }
    }
    public object Info(){var current=Read();var state=Path.Combine(AppEnvironment.Library(root),"library-state.json");var count=File.Exists(state)?JsonNode.Parse(File.ReadAllText(state))?["Cards"]?.AsObject().Count??0:0;return new{current.GameRoot,current.Account,Accounts=Accounts(current.GameRoot),LibraryCards=count,DataRoot=root,Portable=AppEnvironment.Portable(root)};}
}
