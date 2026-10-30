using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

public static class SelfTest
{
    public static void Run(string workspace, string output)
    {
        Directory.CreateDirectory(output);
        var tests = new List<object>();
        void Check(string name, bool value) { tests.Add(new { Name = name, Passed = value }); if (!value) throw new Exception(name); }
        var store = new ProjectStore(workspace, Path.Combine(output, "projects"));
        var exporter = new Exporter(store);
        var cards = store.Choices();
        Check("读取实际中文素材库", cards.Count > 500 && cards.Single(c => c.Id == 21460).Name == "闪刀姬＝零萝");
        var project = store.Create(new NewProjectRequest(21460, ""));
        Check("零萝实际25个部件、3页图集", project.Parts.Count == 25 && project.Parts.Select(p => p.Page).Distinct().Count() == 3);
        var root = store.DirectoryFor(project.Id);
        var prepared = Path.Combine(workspace, "卡片素材工作区", "部件替换准备", "闪刀姬＝零萝〔21460〕-20261029-v2");
        var independentlyExtracted = 0;
        foreach (var p in project.Parts)
        {
            using var a = Image.Load<Rgba32>(Path.Combine(root, "templates", p.Key, "raw.png"));
            using var b = Image.Load<Rgba32>(Path.Combine(prepared, "原部件-PMA原字节", $"{p.Number:D2}-{p.Name}.png"));
            if (a.Size != b.Size) throw new Exception("独立模板尺寸不一致：" + p.Name);
            for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++) if (a[x, y] != b[x, y]) throw new Exception("独立模板像素不一致：" + p.Name);
            independentlyExtracted++;
        }
        Check("25个模板与先前独立Pillow实现逐像素相同", independentlyExtracted == 25);
        var pendingRejected = false;
        try { exporter.Export(project, true); } catch (InvalidOperationException) { pendingRejected = true; }
        Check("未确认部件阻止最终导出", pendingRejected);
        var initial = JsonSerializer.SerializeToNode(exporter.Export(project, false))!;
        var initialModel = initial["ModelFolder"]!.GetValue<string>();
        Check("无替换预览的全部文件保持原字节", project.SourceHashes.All(f => ProjectStore.Hash(Path.Combine(initialModel, f.Key)) == f.Value));
        var head = project.Parts[0];
        using (var input = File.OpenRead(Path.Combine(root, "templates", head.Key, "reference.png"))) store.SetArtwork(project, input);
        using var mask = new Image<Rgba32>(head.Width, head.Height, new Rgba32(255,255,255,255));
        using var maskData = new MemoryStream(); mask.SaveAsPng(maskData);
        store.SaveSelection(project, head, Convert.ToBase64String(maskData.ToArray()));
        Check("选区保存并产生透明部件草稿", File.Exists(ProjectStore.Under(root, head.DraftMask)) && File.Exists(ProjectStore.Under(root, head.DraftCutout)) && head.Placement.Width > 0);
        using var replacement = new Image<Rgba32>(head.Width, head.Height, new Rgba32(128,64,32,128));
        using var replacementData = new MemoryStream(); replacement.SaveAsPng(replacementData);
        store.Confirm(project, head, new ConfirmRequest(Convert.ToBase64String(replacementData.ToArray()), head.Placement));
        var firstRevision = head.ConfirmedRevision;
        var firstExport = JsonSerializer.SerializeToNode(exporter.Export(project, false))!;
        var firstModel = firstExport["ModelFolder"]!.GetValue<string>();
        using var replacementPage = Image.Load<Rgba32>(Path.Combine(firstModel,"_replacement_001.png"));
        var exactPma = replacementPage.Width == head.Width && replacementPage.Height == head.Height;
        for(var y=0;y<replacementPage.Height;y++)for(var x=0;x<replacementPage.Width;x++)
            if(replacementPage[x,y]!=new Rgba32(64,32,16,128))exactPma=false;
        Check("替换部件独立贴图完整保存确认画布与PMA",exactPma);
        Check("原图集页逐字节不变，不会污染相邻部件", project.SourceHashes.Where(f=>f.Key.EndsWith(".png")).All(f=>ProjectStore.Hash(Path.Combine(firstModel,f.Key))==f.Value));
        var redirected=Atlas.Parse(File.ReadAllText(Path.Combine(firstModel,project.AtlasFile)));
        Check("原部件名称关联到新贴图而不是旧图", redirected.Regions[head.Name].Page=="_replacement_001.png");
        store.Confirm(project, project.Parts[20], null);
        store.Confirm(project, head, null);
        store.Restore(project, head, firstRevision);
        var restored = store.Load(project.Id);
        Check("重做历史、恢复和其他步骤进度持久化", restored.Parts[0].Status == "replaced" && restored.Parts[0].Revisions.Count == 2 && restored.Parts[20].Status == "original");
        var restoredExport = JsonSerializer.SerializeToNode(exporter.Export(restored, false))!;
        Check("恢复旧版本导出的贴图一致", ProjectStore.Hash(Path.Combine(restoredExport["ModelFolder"]!.GetValue<string>(), "_replacement_001.png")) == ProjectStore.Hash(Path.Combine(firstModel, "_replacement_001.png")));
        foreach (var p in restored.Parts.Where(p => p.Status == "pending")) store.Confirm(restored, p, null);
        var final = JsonSerializer.SerializeToNode(exporter.Export(restored, true))!;
        Check("全部确认后导出独立动画包", File.Exists(Path.Combine(final["Folder"]!.GetValue<string>(), "导出完成标记.txt")) && restored.Parts.All(p => p.Status != "pending"));
        Check("重复导出保留历史文件夹", restored.Exports.Distinct().Count() == restored.Exports.Count && restored.Exports.Count >= 4);
        Check("项目原素材未改写", restored.SourceHashes.All(f => ProjectStore.Hash(Path.Combine(root,"source",f.Key)) == f.Value));
        var sourceFolder = Path.GetDirectoryName(Path.Combine(restored.SourceVersion,restored.Variant))!;
        Check("素材库原文件未改写", restored.SourceHashes.All(f => ProjectStore.Hash(Path.Combine(sourceFolder,f.Key)) == f.Value));
        var baronne = store.Create(new NewProjectRequest(16386, ""));
        Check("鲜花女男爵多页模板也可创建", baronne.Parts.Count > 25 && baronne.Parts.Select(p => p.Page).Distinct().Count() == 4);
        // A missing pixel inside the mesh must clear the original atlas pixel, not reveal a background fill.
        using var footprint = Image.Load<Rgba32>(Path.Combine(root, "templates", head.Key, "mesh.png"));
        var holeX = -1; var holeY = -1;
        for (var y = head.Height / 3; y < head.Height - 4 && holeX < 0; y++)
            for (var x = head.Width / 3; x < head.Width - 4 && holeX < 0; x++)
            {
                var interior = true;
                for (var dy = -3; dy <= 3; dy++) for (var dx = -3; dx <= 3; dx++)
                    if (footprint[x + dx, y + dy].A == 0) interior = false;
                if (interior) { holeX = x; holeY = y; }
            }
        Check("找到真实网格内部的透明缺口测试点", holeX >= 0);
        using var withHole = new Image<Rgba32>(head.Width, head.Height, new Rgba32(200, 60, 80, 255));
        withHole[holeX, holeY] = default;
        using var withHoleBytes = new MemoryStream(); withHole.SaveAsPng(withHoleBytes);
        store.Confirm(restored, restored.Parts[0], new ConfirmRequest(Convert.ToBase64String(withHoleBytes.ToArray()), head.Placement));
        var holeExport = JsonSerializer.SerializeToNode(exporter.Export(restored, false))!;
        using var holePage = Image.Load<Rgba32>(Path.Combine(holeExport["ModelFolder"]!.GetValue<string>(), "_replacement_001.png"));
        using var holePart = holePage.Clone();
        Check("透明缺口经过确认、PMA与图集回填后仍为全透明", holePart[holeX, holeY] == default(Rgba32));
        var holeRevision=restored.Parts[0].ConfirmedRevision;
        store.Confirm(restored,restored.Parts[0],null,empty:true);
        Check("空素材是独立确认状态且清除草稿引用",restored.Parts[0].Status=="empty"&&restored.Parts[0].DraftCutout=="");
        var emptyExport=JsonSerializer.SerializeToNode(exporter.Export(restored,true))!;
        using var blank=Image.Load<Rgba32>(Path.Combine(emptyExport["ModelFolder"]!.GetValue<string>(),"_replacement_001.png"));
        var allClear=true;for(var y=0;y<blank.Height;y++)for(var x=0;x<blank.Width;x++)if(blank[x,y]!=default(Rgba32))allClear=false;
        Check("空素材整张独立贴图RGBA全零",allClear);
        Check("空素材仍保留原骨骼及动作字节",ProjectStore.Hash(Path.Combine(emptyExport["ModelFolder"]!.GetValue<string>(),restored.SkeletonFile))==restored.SourceHashes[restored.SkeletonFile]);
        store.Restore(restored,restored.Parts[0],holeRevision);
        Check("空素材确认后可恢复先前替换版本",restored.Parts[0].Status=="replaced"&&restored.Parts[0].ConfirmedRevision==holeRevision);
        // The crop must be exactly source alpha multiplied by an irregular manual mask, not an inferred silhouette.
        using var art=Image.Load<Rgba32>(Path.Combine(root,"artwork.png"));
        using var irregular=new Image<Rgba32>(art.Width,art.Height);
        for(var y=0;y<art.Height;y++)for(var x=0;x<art.Width;x++)if(x<art.Width/2||y>art.Height/2)irregular[x,y]=new Rgba32(255,255,255,255);
        using var irregularBytes=new MemoryStream();irregular.SaveAsPng(irregularBytes);
        var selected=JsonSerializer.SerializeToNode(store.SaveSelection(restored,restored.Parts[0],Convert.ToBase64String(irregularBytes.ToArray())))!;
        var crop=selected["Crop"]!;var left=crop["Left"]!.GetValue<int>();var top=crop["Top"]!.GetValue<int>();
        using var cut=Image.Load<Rgba32>(ProjectStore.Under(root,restored.Parts[0].DraftCutout));
        var faithful=true;var excluded=0;
        for(var y=0;y<cut.Height;y++)for(var x=0;x<cut.Width;x++){
            var input=art[x+left,y+top];var coverage=irregular[x+left,y+top].A;
            if(cut[x,y].A!=(input.A*coverage+127)/255)faithful=false;
            if(input.A>0&&coverage==0)excluded++;
        }
        Check("任意形状裁片严格遵循涂抹Alpha且未选部分透明",faithful&&excluded>0);
        var opaqueProject=store.Create(new NewProjectRequest(21460,""));
        var opaqueRoot=store.DirectoryFor(opaqueProject.Id);var opaquePart=opaqueProject.Parts[0];
        using var opaqueArt=new Image<Rgba32>(head.Width,head.Height,new Rgba32(255,0,255,255));
        using var painted=new Image<Rgba32>(head.Width,head.Height);
        for(var y=20;y<head.Height-20;y++)for(var x=20;x<head.Width-20;x++)
            if(x<head.Width/3||y<head.Height/3){painted[x,y]=new Rgba32(255,255,255,255);opaqueArt[x,y]=new Rgba32(20,220,70,255);}
        using var opaqueBytes=new MemoryStream();opaqueArt.SaveAsPng(opaqueBytes,new SixLabors.ImageSharp.Formats.Png.PngEncoder{ColorType=SixLabors.ImageSharp.Formats.Png.PngColorType.Rgb});opaqueBytes.Position=0;store.SetArtwork(opaqueProject,opaqueBytes);
        using var paintedBytes=new MemoryStream();painted.SaveAsPng(paintedBytes);
        store.SaveSelection(opaqueProject,opaquePart,Convert.ToBase64String(paintedBytes.ToArray()));
        using var actualCutout=Image.Load<Rgba32>(ProjectStore.Under(opaqueRoot,opaquePart.DraftCutout));
        var noBackground=true;var clearCount=0;
        for(var y=0;y<actualCutout.Height;y++)for(var x=0;x<actualCutout.Width;x++){
            var c=actualCutout[x,y];if(c.A==0){clearCount++;if(c!=default(Rgba32))noBackground=false;}
            else if(c!=new Rgba32(20,220,70,255))noBackground=false;
        }
        Check("不透明洋红背景的L形涂抹裁片未选部分RGBA全零",noBackground&&clearCount>0);
        var exactPlacement=new Placement{X=head.Width/2d,Y=head.Height/2d,Width=actualCutout.Width,Height=actualCutout.Height};
        store.ConfirmCutout(opaqueProject,opaquePart,exactPlacement);
        var exactRevision=JsonFile.Read<Revision>(ProjectStore.Under(opaqueRoot,opaquePart.ConfirmedRevision));
        using var canonical=Image.Load<Rgba32>(ProjectStore.Under(opaqueRoot,exactRevision.Aligned));
        var exactCanvas=true;var actualPixels=0;
        for(var y=0;y<canonical.Height;y++)for(var x=0;x<canonical.Width;x++){
            if(canonical[x,y].A==0){if(canonical[x,y]!=default(Rgba32))exactCanvas=false;}
            else{actualPixels++;if(canonical[x,y]!=new Rgba32(20,220,70,255))exactCanvas=false;}
        }
        Check("后台从镂空裁片重建确认画布只含涂抹色不含背景",exactCanvas&&actualPixels>0);
        using var scaled=CutoutRenderer.Render(actualCutout,new Placement{X=head.Width/2d,Y=head.Height/2d,Width=actualCutout.Width*.5,Height=actualCutout.Height*.5},head.Width,head.Height);
        var box50=ProjectStore.VisibleBounds(scaled);
        Check("50百分比等比缩放且框架外透明",box50.Width<=Math.Ceiling(actualCutout.Width*.5)+2&&box50.Height<=Math.Ceiling(actualCutout.Height*.5)+2&&scaled[0,0]==default(Rgba32));
        using var turned=CutoutRenderer.Render(actualCutout,new Placement{X=head.Width/2d,Y=head.Height/2d,Width=actualCutout.Width*.7,Height=actualCutout.Height*.7,Rotation=37,Flip=true},head.Width,head.Height);
        var noMagenta=true;for(var y=0;y<turned.Height;y++)for(var x=0;x<turned.Width;x++){var c=turned[x,y];if(c.A>0&&(c.R>30||c.B>80))noMagenta=false;}
        Check("缩放旋转翻转采用透明边界采样无背景串色",noMagenta);
        var canonicalExport=JsonSerializer.SerializeToNode(exporter.Export(opaqueProject,false))!;
        using var exportedCutout=Image.Load<Rgba32>(Path.Combine(canonicalExport["ModelFolder"]!.GetValue<string>(),"_replacement_001.png"));
        var exportMatches=true;using var expected=canonical.Clone();Atlas.ToPma(expected);
        for(var y=0;y<expected.Height;y++)for(var x=0;x<expected.Width;x++)if(expected[x,y]!=exportedCutout[x,y])exportMatches=false;
        Check("后台镂空确认与导出PMA页逐像素一致",exportMatches);
        var gameExporter=new GameReplacementExporter(store,exporter);
        var rejectPending=false;try{gameExporter.Export(opaqueProject);}catch(InvalidOperationException){rejectPending=true;}
        Check("游戏替换导出同样阻止未确认部件",rejectPending);
        foreach(var pp in opaqueProject.Parts.Where(x=>x.Status=="pending"))store.Confirm(opaqueProject,pp,null);
        var replacementBundle=JsonSerializer.SerializeToNode(gameExporter.Export(opaqueProject))!;
        var gameFolder=replacementBundle["Folder"]!.GetValue<string>();
        var install=JsonNode.Parse(File.ReadAllText(Path.Combine(gameFolder,"替换清单.json")))!;
        Check("真实游戏同名Bundle与备份生成并完成回读验证",replacementBundle["BundleCount"]!.GetValue<int>()==2&&install["Verified"]!.GetValue<bool>()&&File.Exists(Path.Combine(gameFolder,"替换文件完成标记.txt")));
        Check("游戏原文件与备份SHA保持一致未安装",install["Files"]!.AsArray().All(f=>ProjectStore.Hash(f!["Target"]!.GetValue<string>())==f["OriginalSha256"]!.GetValue<string>()&&ProjectStore.Hash(Path.Combine(gameFolder,f["Backup"]!.GetValue<string>()))==f["OriginalSha256"]!.GetValue<string>()));
        var packedAtlas=Atlas.Parse(File.ReadAllText(Path.Combine(gameFolder,"游戏图集核对",opaqueProject.AtlasFile)));
        var originalAtlas=Atlas.Parse(File.ReadAllText(Path.Combine(opaqueRoot,"source",opaqueProject.AtlasFile)));
        Check("游戏导出保留原Atlas页名称顺序及区域名称集合",packedAtlas.Pages.Keys.SequenceEqual(originalAtlas.Pages.Keys)&&packedAtlas.Regions.Keys.Order().SequenceEqual(originalAtlas.Regions.Keys.Order()));
        var redirectedRegion=packedAtlas.Regions[opaquePart.RegionName];
        Check("替换区域独立于原共享像素且其他区域元数据不变",redirectedRegion.Fields["bounds"]!=originalAtlas.Regions[opaquePart.RegionName].Fields["bounds"]&&originalAtlas.Regions.Where(r=>r.Key!=opaquePart.RegionName).All(r=>r.Value.Fields.OrderBy(x=>x.Key).SequenceEqual(packedAtlas.Regions[r.Key].Fields.OrderBy(x=>x.Key))));
        var bad=new GameReplacementExporter.Locator("test",0,long.MaxValue,"not-found","Texture2D");
        var testBundle=Directory.GetFiles(Path.Combine(gameFolder,"替换文件"),"*",SearchOption.AllDirectories).First();
        var copyBundle=Path.Combine(output,"bad-target.bundle");File.Copy(testBundle,copyBundle);
        var badHash=ProjectStore.Hash(copyBundle);var badRejected=false;
        try{GameReplacementExporter.Rewrite(copyBundle,[new(bad,null,[]) ]);}catch(InvalidDataException){badRejected=true;}
        Check("错误PathID拒绝写出且输入Bundle保持原样",badRejected&&ProjectStore.Hash(copyBundle)==badHash);
        var allEmptyProject=store.Create(new NewProjectRequest(21460,""));
        foreach(var pp in allEmptyProject.Parts)store.Confirm(allEmptyProject,pp,null,empty:true);
        var allEmptyPackage=JsonSerializer.SerializeToNode(gameExporter.Export(allEmptyProject))!;
        Check("整套25部件可导出4个同名Bundle及原文件备份",allEmptyPackage["BundleCount"]!.GetValue<int>()==4);
        var emptyModel=Path.Combine(allEmptyPackage["Folder"]!.GetValue<string>(),"游戏图集核对");
        var fullAtlas=Atlas.Parse(File.ReadAllText(Path.Combine(emptyModel,allEmptyProject.AtlasFile)));
        var emptyRegions=true;
        foreach(var pp in allEmptyProject.Parts){var bounds=fullAtlas.Regions[pp.RegionName].Fields["bounds"].Split(',').Select(int.Parse).ToArray();using var page=Image.Load<Rgba32>(Path.Combine(emptyModel,pp.Page));for(var y=0;y<bounds[3];y++)for(var x=0;x<bounds[2];x++)if(page[x+bounds[0],y+bounds[1]]!=default(Rgba32))emptyRegions=false;}
        Check("整套替换图集的全部新区域RGBA全零且保持原页数",emptyRegions&&fullAtlas.Pages.Count==3);
        JsonFile.Write(Path.Combine(output, "selftest-result.json"), new { Passed = tests.Count, Tests = tests, Project = project.Id, OpaqueFixture=opaqueProject.Id, GameExport=gameFolder, AllEmptyGameExport=allEmptyPackage, Final = final, Baronne = baronne.Id });
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = tests.Count, Output = output, UiFixtureProject = project.Id }, JsonFile.Options));
    }
}
