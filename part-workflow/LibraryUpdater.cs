using CardAssetWorkbench;

namespace PartWorkflow;
public sealed class LibraryUpdater(ProjectStore store,InstallSettings settings,int[]? testOnly=null)
{
    readonly object gate=new();readonly List<string> messages=[];CancellationTokenSource? cancellation;
    string phase="idle",error="",job="";bool running;object? report;
    public object Snapshot(){lock(gate)return new{Running=running,Phase=phase,Error=error,Job=job,Messages=messages.ToArray(),Report=report};}
    public bool Running{get{lock(gate)return running;}}
    void Log(string message){lock(gate){messages.Add(DateTime.Now.ToString("HH:mm:ss")+" "+message);if(messages.Count>100)messages.RemoveAt(0);}File.AppendAllText(Path.Combine(AppEnvironment.Runtime(store.Workspace),"素材更新.log"),DateTime.Now+" "+message+Environment.NewLine);}
    public object Start()
    {
        GameSettings config;
        lock(gate){if(running)throw new InvalidOperationException("素材更新正在进行");config=settings.Save(settings.Read());running=true;phase="scan";error="";report=null;messages.Clear();job=DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");cancellation=new();}
        var token=cancellation.Token;
        _=Task.Run(()=>
        {
            try
            {
                var root=store.Workspace;var cache=Path.Combine(root,"数据","索引");Directory.CreateDirectory(cache);
                // Rebase cached game paths after moving the portable folder; no scan occurs before this explicit action.
                var indexPath=Path.Combine(cache,"index.json");
                if(File.Exists(indexPath))
                {
                    var old=AssetIndex.Load(indexPath);if(!old.GameRoot.Equals(config.GameRoot,StringComparison.OrdinalIgnoreCase)||old.Account!=config.Account)
                    {
                        var copy=Path.Combine(AppEnvironment.Runtime(root),"更新前索引-"+job+".json");File.Copy(indexPath,copy);
                        old.Bundles=old.Bundles.Select(b=>b with{Path=MapPath(b.Path,config)}).GroupBy(b=>b.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
                        old.FailedFiles=old.FailedFiles.Select(f=>f with{Path=MapPath(f.Path,config)}).GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();old.GameRoot=config.GameRoot;old.Account=config.Account;AssetIndex.Save(indexPath,old);
                    }
                }
                Log("读取现有索引；仅检查新增或变动资源。");
                var index=AssetIndex.Scan(config.GameRoot,config.Account,cache,Path.Combine(root,"不使用数据库.db"),Log,token);
                lock(gate)phase="extract";
                var result=File.Exists(Path.Combine(AppEnvironment.Library(root),"catalog-only.json"))?PortableCatalog.Update(index,AppEnvironment.Library(root),Log,token,testOnly):BulkLibrary.Run(index,AppEnvironment.Library(root),Log,token,testOnly);
                lock(store.Gate)store.InvalidateChoices();
                lock(gate){report=result;phase=result.Failed>0?"partial":"completed";}
                Log($"卡名目录已刷新：新增{result.Added}，更新{result.Updated}，跳过{result.Skipped}，失败{result.Failed}。");
            }
            catch(OperationCanceledException){lock(gate)phase="cancelled";Log("更新已停止，已经完成的素材保留。");lock(store.Gate)store.InvalidateChoices();}
            catch(Exception ex){lock(gate){phase="failed";error=ex.Message;}Log("更新失败："+ex.Message);}
            finally{lock(gate){running=false;cancellation?.Dispose();cancellation=null;}}
        });return Snapshot();
    }
    public void Cancel(){lock(gate)cancellation?.Cancel();}
    static string MapPath(string path,GameSettings config)
    {
        var normalized=path.Replace('\\','/');var start=normalized.IndexOf("/LocalData/",StringComparison.OrdinalIgnoreCase);
        if(start>=0){var tail=normalized[(start+11)..];var slash=tail.IndexOf('/');return ProjectStore.Under(config.GameRoot,"LocalData/"+config.Account+tail[slash..]);}
        start=normalized.IndexOf("/masterduel_Data/",StringComparison.OrdinalIgnoreCase);if(start<0)throw new InvalidDataException("旧索引含无法映射的游戏路径");return ProjectStore.Under(config.GameRoot,normalized[(start+1)..]);
    }
}
