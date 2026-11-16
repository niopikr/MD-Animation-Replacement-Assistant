using System.Text.Json.Nodes;
namespace PartWorkflow;
public static class CompactSelfTest
{
 public static void Run(string root,string output)
 {
  Directory.CreateDirectory(output);var tests=new List<object>();void Check(string n,bool yes){tests.Add(new{Name=n,Passed=yes});if(!yes)throw new Exception(n);}
  var store=new ProjectStore(root,Path.Combine(output,"projects"));var p=store.Create(new(3801,""));
  store.Confirm(p,p.Parts[0],null,empty:true);var revision=p.Parts[0].ConfirmedRevision;
  store.KeepPending(p);Check("一键保留全部剩余部件且不覆盖空素材",p.Parts[0].Status=="empty"&&p.Parts[0].ConfirmedRevision==revision&&p.Parts.Skip(1).All(x=>x.Status=="original"));
  var counts=p.Parts.Select(x=>x.Revisions.Count).ToArray();store.KeepPending(p);Check("重复一键确认不增加历史",counts.SequenceEqual(p.Parts.Select(x=>x.Revisions.Count)));
  var compact=new CompactExport(store,new Exporter(store));var ready=compact.Export(p);
  Check("一次导出直接生成替换文件",ready.Ready&&Directory.GetFileSystemEntries(ready.Folder).Select(Path.GetFileName).SequenceEqual(new[]{"替换"}));
  Check("替换从0000开始",Directory.GetDirectories(Path.Combine(ready.Folder,"替换")).Select(Path.GetFileName).SequenceEqual(new[]{"0000"}));
  var details=JsonNode.Parse(File.ReadAllText(ProjectStore.Under(store.DirectoryFor(p.Id),ready.Relative+"/替换清单.json")))!;
  Check("高清低清替换文件完整",ready.CompanionCount>0&&details["Files"]!.AsArray().All(f=>ProjectStore.Hash(Path.Combine(ready.Folder,"替换",f!["File"]!.GetValue<string>()))==f["ReplacementSha256"]!.GetValue<string>()));
  Check("打开导出只定位精简目录",CompactExport.OpenFolder(store,p,ready.Relative)==ready.Folder);
  var original=store.Create(new(3801,""));store.KeepPending(original);var whole=compact.Export(original);Check("全使用原图也可导出",whole.Changes==0&&Directory.GetFiles(Path.Combine(whole.Folder,"替换"),"*",SearchOption.AllDirectories).Length>=2);
  JsonFile.Write(Path.Combine(output,"selftest-result.json"),new{Passed=tests.Count,Tests=tests,ready.Folder,ready.BundleCount});
 }
}
