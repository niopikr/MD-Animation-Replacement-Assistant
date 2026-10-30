using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
  var compact=new CompactExport(store,new Exporter(store));var pending=compact.Begin(p);
  Check("图像完成前不暴露半成品目录",!pending.Ready&&!Directory.Exists(pending.Folder));
  string Frame(int w,int h){using var image=new Image<Rgba32>(w,h,new Rgba32(0,200,30,255));using var ms=new MemoryStream();image.SaveAsPng(ms);return Convert.ToBase64String(ms.ToArray());}
  var invalid=false;try{compact.Complete(p,new(pending.Relative,Frame(4,4)));}catch(InvalidDataException){invalid=true;}Check("错误尺寸尾帧拒绝完成导出",invalid&&!Directory.Exists(pending.Folder));
  var ready=compact.Complete(p,new(pending.Relative,Frame(1024,768)));Check("导出总目录严格只有三项",Directory.GetFileSystemEntries(ready.Folder).Select(Path.GetFileName).Order().SequenceEqual(new[]{"原始","替换","替换后尾帧.png"}.Order()));
  Check("替换原始均从0000开始",Directory.GetDirectories(Path.Combine(ready.Folder,"替换")).Select(Path.GetFileName).SequenceEqual(new[]{"0000"})&&Directory.GetDirectories(Path.Combine(ready.Folder,"原始")).Select(Path.GetFileName).SequenceEqual(new[]{"0000"}));
  var details=JsonNode.Parse(File.ReadAllText(ProjectStore.Under(store.DirectoryFor(p.Id),pending.Relative+"/替换清单.json")))!;
  Check("高清低清文件和原备份哈希完整",ready.CompanionCount>0&&details["Files"]!.AsArray().All(f=>ProjectStore.Hash(Path.Combine(ready.Folder,"替换",f!["File"]!.GetValue<string>()))==f["ReplacementSha256"]!.GetValue<string>()&&ProjectStore.Hash(Path.Combine(ready.Folder,"原始",f["File"]!.GetValue<string>()))==f["OriginalSha256"]!.GetValue<string>()));
  Check("实际游戏未写入",details["Files"]!.AsArray().All(f=>ProjectStore.Hash(f!["Target"]!.GetValue<string>())==f["BeforeExportSha256"]!.GetValue<string>()));
  Check("打开导出只定位精简目录",CompactExport.OpenFolder(store,p,pending.Relative)==ready.Folder);
  var retry=compact.Complete(p,new(pending.Relative,Frame(1024,768)));Check("完成请求可安全重试",retry.Ready&&Directory.GetFileSystemEntries(retry.Folder).Length==3);
  var original=store.Create(new(3801,""));store.KeepPending(original);var untouched=compact.Begin(original);var whole=compact.Complete(original,new(untouched.Relative,Frame(1024,768)));Check("全使用原图也可导出",whole.Changes==0&&Directory.GetFiles(Path.Combine(whole.Folder,"替换"),"*",SearchOption.AllDirectories).Length>=2);
  JsonFile.Write(Path.Combine(output,"selftest-result.json"),new{Passed=tests.Count,Tests=tests,ready.Folder,ready.BundleCount});
 }
}
