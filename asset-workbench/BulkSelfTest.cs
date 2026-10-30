using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CardAssetWorkbench;

public static class BulkSelfTest
{
    public static int Run(Action<string> log)
    {
        var index=AssetIndex.Load(Path.Combine(Program.Cache,"index.json"));
        var root=Path.Combine(Program.Workspace,".dev-tools","bulk-extraction-check-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));Directory.CreateDirectory(root);
        var passed=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new Exception("检查失败："+label);passed.Add(label);log("通过："+label);}
        Check(index.ChineseNames.GetValueOrDefault(16386)=="鲜花之女男爵","采用游戏 zh-cn 名表，不采用常用简称");
        Check(BulkLibrary.SafeComponent("CON")=="_CON"&&BulkLibrary.SafeComponent("卡名:测试. ")=="卡名_测试","Windows 名称清理及保留名称处理");
        Check(BulkLibrary.SafeComponent("同名")+"〔1〕"!=BulkLibrary.SafeComponent("同名")+"〔2〕","同名异画用卡号区分");
        bool escaped=false;try{BulkLibrary.Inside(root,"../escape");}catch(IOException){escaped=true;}Check(escaped,"拒绝清单路径越界");
        bool gameGuard=false;try{AssetIndex.GuardOutsideGame(index,Path.Combine(index.GameRoot,"test-output"));}catch(IOException){gameGuard=true;}Check(gameGuard,"拒绝输出到游戏安装目录");
        var ids=AssetIndex.AnimationIds(index);Check(ids.Contains(16386)&&!ids.Contains(163862),"图集页号不会被识别为新卡号");
        var associated=BulkLibrary.BuildAssociations(index,ids);
        foreach(var id in new[]{16386,3401,4007,14741,23487}.Where(ids.Contains))
            Check(associated[id].Select(b=>b.Path).Order().SequenceEqual(AssetIndex.Related(index,id).Select(b=>b.Path).Order()),$"反向索引与既有检索结果一致（{id}）");
        var fake=new GameIndex{Bundles=[new BundleRecord("new","LocalData",1,1,["assets/resourcesassetbundle/duel/timeline/duel/monstercutin/tcg/p29998/highend_hd/p29998.png"],[])]};
        Check(AssetIndex.AnimationIds(fake).SequenceEqual(new[]{29998}),"自动识别新卡号，不使用固定动画白名单");
        var sources=new[]{new SourceDigest("bundle","path-a","abc",1,1)};
        Check(BulkLibrary.Fingerprint(1,"测试",sources)==BulkLibrary.Fingerprint(1,"测试",[sources[0] with{Path="moved",ModifiedTicks=9}]),"内容指纹不依赖绝对路径或单纯时间变化");
        Check(BulkLibrary.Fingerprint(1,"测试",sources)!=BulkLibrary.Fingerprint(1,"测试",[sources[0] with{Sha256="changed"}]),"资源内容变化会更新版本指纹");
        var library=Path.Combine(root,"fixture-library");
        var first=BulkLibrary.Run(index,library,log,CancellationToken.None,[16386]);
        Check(first.Added==1&&first.Failed==0&&first.TexturesWritten==8,"真实鲜花素材首次提取及八图像素/透明度校验");
        var before=JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(Path.Combine(library,"library-state.json")))!;
        Check(before.Cards[16386].PreviewCount==2,"高清四页及低清一页均配齐预览组");
        var second=BulkLibrary.Run(index,library,log,CancellationToken.None,[16386]);
        Check(second.Skipped==1&&second.Added==0&&second.Updated==0&&second.TexturesWritten==0,"重复执行跳过全部未变化素材");
        var png=before.Cards[16386].Files.First(f=>f.File.EndsWith(".png",StringComparison.OrdinalIgnoreCase));
        var edited=BulkLibrary.Inside(library,png.File);using(var stream=new FileStream(edited,FileMode.Append)){stream.WriteByte(0);}
        var editedHash=AssetIndex.Sha(edited);
        var third=BulkLibrary.Run(index,library,log,CancellationToken.None,[16386]);
        var after=JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(Path.Combine(library,"library-state.json")))!;
        Check(third.Updated==1&&third.Failed==0&&after.Cards[16386].VersionFolder!=before.Cards[16386].VersionFolder,"检测交付文件变化并生成独立新版本");
        Check(AssetIndex.Sha(edited)==editedHash,"保留用户编辑过的旧文件，不覆盖");
        var missing=index.Bundles.First(b=>associated[16386].Any(a=>a.Path==b.Path));
        var bad=new GameIndex{GameRoot=index.GameRoot,Account=index.Account,ChineseNames=index.ChineseNames,Cards=index.Cards,Bundles=index.Bundles.Select(b=>b.Path==missing.Path?b with{Path=Path.Combine(root,"不存在的源文件")}:b).ToList()};
        var failed=BulkLibrary.Run(bad,library,log,CancellationToken.None,[16386]);
        var preserved=JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(Path.Combine(library,"library-state.json")))!;
        Check(failed.Failed==1&&preserved.Cards[16386].VersionFolder==after.Cards[16386].VersionFolder,"缺少源文件时记录失败并保留已完成版本");
        var recovered=BulkLibrary.Run(index,library,log,CancellationToken.None,[16386],true);
        Check(recovered.Skipped==1&&recovered.Failed==0,"故障恢复后可继续，深度校验不重建未变化内容");
        var parallelLibrary=Path.Combine(root,"parallel-fixture");
        var parallel=BulkLibrary.Run(index,parallelLibrary,log,CancellationToken.None,[16386,4007]);
        var parallelState=JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(Path.Combine(parallelLibrary,"library-state.json")))!;
        Check(parallel.Added==2&&parallel.Failed==0&&parallelState.Cards.Count==2,"两张真实卡并发提取，状态与输出均完整");
        var parallelAgain=BulkLibrary.Run(index,parallelLibrary,log,CancellationToken.None,[16386,4007]);
        Check(parallelAgain.Skipped==2&&parallelAgain.Updated==0,"并发增量检查跳过全部已完成卡");
        AssetIndex.Save(Path.Combine(root,"selftest-result.json"),new{Passed=passed.Count,Checks=passed,Fixture=root,GameWrite=false});
        log($"针对性检查通过 {passed.Count} 项；证据：{root}");return 0;
    }
}
