using System.IO;

namespace CardAssetWorkbench;

public static class Program
{
    public static readonly string Workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public static readonly string Work = Path.Combine(Workspace,"卡片素材工作区");
    public static readonly string Cache = Path.Combine(Work,"索引");
    public static readonly string Game = Environment.GetEnvironmentVariable("MD_GAME_ROOT") ?? "";
    public static readonly string Database = Path.Combine(Workspace,"master-duel-modding","database.db");
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Work);
            if(args.Length>0)
            {
                var logGate=new object();
                void Log(string s){lock(logGate){Console.WriteLine(s);File.AppendAllText(Path.Combine(Work,"运行记录.txt"),$"{DateTime.Now:HH:mm:ss} {s}\n");}}
                if(args[0]=="scan")
                {
                    var index=AssetIndex.Scan(Game,args.ElementAtOrDefault(1)??Environment.GetEnvironmentVariable("MD_GAME_ACCOUNT")??"",Cache,Database,Log,CancellationToken.None);
                    return index.Errors.Count>0?2:0;
                }
                if(args[0]=="export")
                {
                    var index=AssetIndex.Load(Path.Combine(Cache,"index.json"));
                    var output=AssetIndex.Export(index,Resolve(index,args[1]),Path.Combine(Work,"导出"),Log);
                    File.WriteAllText(Path.Combine(Work,"最近导出.txt"),output);return 0;
                }
                if(args[0]=="search")
                {
                    var index=AssetIndex.Load(Path.Combine(Cache,"index.json"));
                    foreach(var card in index.Cards.Where(c=>c.Id.ToString()==args[1]||c.Names.Any(n=>n.Contains(args[1],StringComparison.OrdinalIgnoreCase))))
                        Console.WriteLine($"{card.Id}\t{string.Join(" / ",card.Names)}\t关联 Bundle {AssetIndex.Related(index,card.Id).Count}");
                    return 0;
                }
                if(args[0]=="catalog")
                {
                    AssetIndex.WriteCatalog(AssetIndex.Load(Path.Combine(Cache,"index.json")),Cache);
                    return 0;
                }
                if(args[0]=="verify")
                {
                    Verification.Run(args.ElementAtOrDefault(1)??File.ReadAllText(Path.Combine(Work,"最近导出.txt")));
                    return 0;
                }
                if(args[0] is "bulk" or "update-library")
                {
                    var library=Path.GetFullPath(args.ElementAtOrDefault(1)??Path.Combine(Work,"全动画卡片素材库"));
                    var force=args.Contains("--verify-all");
                    var index=AssetIndex.Load(Path.Combine(Cache,"index.json"));
                    if(args[0]=="update-library")
                    {
                        AssetIndex.GuardOutsideGame(index,library);Directory.CreateDirectory(library);
                        var backup=Path.Combine(library,"运行记录","索引快照-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));Directory.CreateDirectory(backup);
                        File.Copy(Path.Combine(Cache,"index.json"),Path.Combine(backup,"index.json"));
                        var gameRoot=Environment.GetEnvironmentVariable("MD_GAME_ROOT")??Game;
                        var accountName=Environment.GetEnvironmentVariable("MD_GAME_ACCOUNT")??index.Account;
                        index=AssetIndex.Scan(gameRoot,accountName,Cache,Database,Log,CancellationToken.None);
                    }
                    int[]? only=null;var onlyArg=args.FirstOrDefault(a=>a.StartsWith("--ids="));if(onlyArg is not null)only=onlyArg[6..].Split(',').Select(int.Parse).ToArray();
                    var result=BulkLibrary.Run(index,library,Log,CancellationToken.None,only,force);return result.Failed>0?2:0;
                }
                if(args[0]=="bulk-selftest")return BulkSelfTest.Run(Log);
            }
            Console.WriteLine("用法：CardAssetExtractor scan [账号目录名] | search 卡名 | export 卡号或卡名 | verify");return 0;
        }
        catch(Exception ex)
        {
            Directory.CreateDirectory(Work); File.WriteAllText(Path.Combine(Work,"错误详情.txt"),ex.ToString());
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    static int Resolve(GameIndex index,string query)
    {
        if(int.TryParse(query,out var id))return id;
        var exact=index.Cards.Where(c=>c.Names.Any(n=>n.Equals(query,StringComparison.OrdinalIgnoreCase))).ToList();
        var found=exact.Count>0?exact:index.Cards.Where(c=>c.Names.Any(n=>n.Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
        if(found.Count!=1)throw new IOException(found.Count==0?"没有匹配的卡名；请尝试卡号或其他语言名称。":"卡名匹配多张卡，请指定卡号："+string.Join(" / ",found.Select(c=>$"{c.Id} {c.Name}")));
        return found[0].Id;
    }
}
