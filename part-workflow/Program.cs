using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PartWorkflow;
using SixLabors.ImageSharp;

var workspace = args.SkipWhile(a => a != "--workspace").Skip(1).FirstOrDefault() ?? (AppEnvironment.Portable(AppContext.BaseDirectory)?AppContext.BaseDirectory:Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));
if (!File.Exists(Path.Combine(workspace, "PROJECT_STATUS.md"))&&!AppEnvironment.Portable(workspace)) throw new DirectoryNotFoundException("找不到程序数据目录，请保留EXE、portable.json和素材文件夹。");
if(OperatingSystem.IsWindows())foreach(var native in new[]{"vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll","msvcp140_1.dll"})
{
    var local=AppEnvironment.Resource(Path.Combine("native",native));if(File.Exists(local))System.Runtime.InteropServices.NativeLibrary.Load(local);
}
Directory.CreateDirectory(AppEnvironment.Runtime(workspace));
AppEnvironment.EnsureDefaultData(workspace);
if(AppEnvironment.Portable(workspace)&&!args.Any(a=>a.Contains("self-test")||a=="--build-catalog-only"))AppDomain.CurrentDomain.UnhandledException+=(_,e)=>NativeDialogs.Error(e.ExceptionObject.ToString()??"启动失败");
if(args.Contains("--build-catalog-only")){var i=Array.IndexOf(args,"--build-catalog-only");PortableCatalog.BuildSeed(args[i+1],args[i+2]);return;}
var activeFile = Path.Combine(AppEnvironment.Runtime(workspace),"md-animation-helper-active.json");
if (!args.Contains("--no-browser") && File.Exists(activeFile))
{
    try
    {
        var previous = JsonFile.Read<Dictionary<string, string>>(activeFile);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Add("X-Workflow-Token", previous["Token"]);
        if ((await client.GetAsync(previous["Url"] + "api/ping")).IsSuccessStatusCode)
        { TryOpenBrowser(previous["Url"] + "#" + previous["Token"]); return; }
    }
    catch { /* Previous host may have been closed. */ }
}
var projectRoot = args.SkipWhile(a => a != "--project-root").Skip(1).FirstOrDefault();
if (projectRoot is not null) projectRoot = ProjectStore.Under(workspace, projectRoot);
var store = new ProjectStore(workspace, projectRoot);
using var sam = new SamService(store);
var exporter = new Exporter(store);
var settings=new InstallSettings(workspace);
var updater=new LibraryUpdater(store,settings);
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var builder = WebApplication.CreateBuilder(args);
// Browser blocked-port lists include some ports Windows can assign for port 0.
var localPort=0;
for(var attempt=0;attempt<20;attempt++)
{
    var candidate=RandomNumberGenerator.GetInt32(20000,60000);
    var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,candidate);
    try{listener.Start();localPort=candidate;break;}
    catch(System.Net.Sockets.SocketException){ }
    finally{listener.Stop();}
}
if(localPort==0)throw new IOException("未找到可用的本机端口，请重试。");
builder.WebHost.UseUrls("http://127.0.0.1:"+localPort);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 100 * 1024 * 1024);
builder.Logging.ClearProviders();
var app = builder.Build();
var web = AppEnvironment.Resource("web");
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'";
    if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/files"))
    {
        var supplied = context.Request.Headers["X-Workflow-Token"].FirstOrDefault() ?? context.Request.Query["t"].FirstOrDefault();
        if (supplied != token) { context.Response.StatusCode = 403; return; }
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (origin is not null && origin != "http://" + context.Request.Host) { context.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch (Exception e)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { Error = e.Message });
        }
        File.AppendAllText(Path.Combine(AppEnvironment.Runtime(workspace), "part-workflow-errors.log"), DateTime.Now + " " + e + Environment.NewLine);
    }
});
app.MapGet("/", () => Results.File(Path.Combine(web, "index.html"), "text/html; charset=utf-8"));
app.MapGet("/app.js", () => Results.File(Path.Combine(web, "app.js"), "text/javascript; charset=utf-8"));
app.MapGet("/preview.js", () => Results.File(Path.Combine(web, "preview.js"), "text/javascript; charset=utf-8"));
app.MapGet("/rigby/{**relative}", (string relative) =>
{
    var file = ProjectStore.Under(Path.Combine(web, "rigby"), relative);
    return Path.GetExtension(file) == ".js" ? Results.File(file, "text/javascript; charset=utf-8") : Results.BadRequest();
});
app.MapGet("/style.css", () => Results.File(Path.Combine(web, "style.css"), "text/css; charset=utf-8"));
app.MapGet("/api/ping", () => Results.Ok(new { Version = "1.5.6" }));
app.MapGet("/api/bootstrap", () => { lock (store.Gate) return Results.Ok(new { Cards = store.Choices(), Projects = store.Recent(), SamAvailable = sam.ModelDirectory.Length > 0,Installation=settings.Info() }); });
app.MapGet("/api/settings",()=>Results.Ok(settings.Info()));
app.MapPost("/api/settings",(GameSettings value)=>{if(updater.Running)throw new InvalidOperationException("更新中不能切换游戏目录");settings.Save(value);return Results.Ok(settings.Info());});
app.MapPost("/api/settings/browse", async (FolderPickerRequest? request) => Results.Ok(new
{
    Path = await NativeDialogs.PickFolder("选择包含 masterduel.exe 的游戏目录", request?.InitialPath ?? settings.Read().GameRoot)
}));
app.MapGet("/api/library/status",()=>Results.Ok(updater.Snapshot()));
app.MapPost("/api/library/update",()=>Results.Ok(updater.Start()));
app.MapPost("/api/library/cancel",()=>{updater.Cancel();return Results.Ok();});
app.MapGet("/api/effects/catalog", () => Results.Ok(EffectCatalog.Read(workspace)));
app.MapPost("/api/effects/extract", async (EffectExportRequest request, HttpContext context) =>
{
    if (updater.Running) throw new InvalidOperationException("目录正在刷新，请等刷新完成再提取。");
    var (content, fileName) = await EffectExport.Create(workspace, settings.Read(), request.CardId, context.RequestAborted);
    context.Response.Headers["X-Workflow-Filename"] = Uri.EscapeDataString(fileName);
    return Results.File(content, "application/zip");
});
app.MapPost("/api/effects/browse", async (FolderPickerRequest? request) => Results.Ok(new
{
    Path = await NativeDialogs.PickFolder("选择含有效果贴图映射.json的素材目录", request?.InitialPath)
}));
app.MapPost("/api/effects/pack", (EffectFolderRequest request, HttpContext context) =>
{
    var (content, fileName) = EffectPackage.Pack(workspace, settings.Read(), request.Folder, request.CardId);
    context.Response.Headers["X-Workflow-Filename"] = Uri.EscapeDataString(fileName);
    return Results.File(content, "application/zip");
});
app.MapPost("/api/library/cache/clear",()=>{lock(store.Gate)return Results.Ok(OnDemandLibrary.ClearUnused(store));});
app.MapPost("/api/projects", (NewProjectRequest request) => { lock (store.Gate) return Results.Ok(store.Create(request)); });
app.MapPost("/api/rigby/export", (NewProjectRequest request, HttpContext context) =>
{
    lock (store.Gate)
    {
        var (content, fileName) = RigbyExport.Create(store, request);
        context.Response.Headers["X-Rigby-Filename"] = Uri.EscapeDataString(fileName);
        return Results.File(content, "application/zip");
    }
});
app.MapPost("/api/rigby/browse", async (FolderPickerRequest? request) => Results.Ok(new
{
    Path = await NativeDialogs.PickFolder("选择已编辑的召唤素材文件夹", request?.InitialPath)
}));
app.MapPost("/api/rigby/pack", (SummonPackRequest request, HttpContext context) =>
{
    if (updater.Running) throw new InvalidOperationException("目录正在刷新，请等刷新完成再打包。");
    lock (store.Gate)
    {
        var (content, fileName, repairs) = SummonPackage.Pack(store, settings.Read(), request);
        context.Response.Headers["X-Workflow-Filename"] = Uri.EscapeDataString(fileName);
        if (repairs.Count > 0)
            context.Response.Headers["X-Workflow-Repairs"] = Uri.EscapeDataString(string.Join("、", repairs));
        return Results.File(content, "application/zip");
    }
});
app.MapGet("/api/projects/{id}", (string id) => { lock (store.Gate) return Results.Ok(store.Load(id)); });
app.MapGet("/files/{id}/{**relative}", (string id, string relative) =>
{
    var file = ProjectStore.Under(store.DirectoryFor(id), relative);
    var mime = Path.GetExtension(file) switch { ".png" => "image/png", ".json" => "application/json", ".atlas" => "text/plain", _ => "" };
    if (mime.Length == 0) return Results.BadRequest();
    return Results.File(file, mime);
});
app.MapPost("/api/projects/{id}/artwork", (string id, HttpRequest request) =>
{
    lock (store.Gate)
    {
        var p = store.Load(id);
        using var data = new MemoryStream(); request.Body.CopyToAsync(data).GetAwaiter().GetResult(); data.Position = 0;
        store.SetArtwork(p, data); return Results.Ok(p);
    }
});
app.MapPost("/api/projects/{id}/step", (string id, StepRequest request) =>
{
    lock (store.Gate) { var p = store.Load(id); p.CurrentPart = Math.Clamp(request.Number, 0, p.Parts.Count - 1); store.Save(p); return Results.Ok(); }
});
app.MapPost("/api/projects/{id}/parts/{number}/selection", (string id, int number, SelectionRequest request) =>
{
    lock (store.Gate) { var p = store.Load(id); return Results.Ok(store.SaveSelection(p, p.Parts.Single(x => x.Number == number), request.Mask)); }
});
app.MapPost("/api/projects/{id}/parts/{number}/placement", (string id, int number, Placement request) =>
{
    lock (store.Gate) { var p = store.Load(id); p.Parts.Single(x => x.Number == number).Placement = request; store.Save(p); return Results.Ok(); }
});
app.MapPost("/api/projects/{id}/parts/{number}/draft-mask", (string id, int number, SelectionRequest request) =>
{
    lock (store.Gate)
    {
        var p = store.Load(id); var part = p.Parts.Single(x => x.Number == number);
        using var image = ProjectStore.DecodePng(request.Mask);
        var root = store.DirectoryFor(id);
        using var art = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(Path.Combine(root, p.Artwork));
        if (art.Size != image.Size) throw new InvalidDataException("选区尺寸错误");
        Directory.CreateDirectory(Path.Combine(root, "draft", part.Key));
        part.DraftMask = $"draft/{part.Key}/mask.png";
        part.DraftCutout = "";
        JsonFile.Png(ProjectStore.Under(root, part.DraftMask), image); store.Save(p); return Results.Ok();
    }
});
app.MapPost("/api/projects/{id}/parts/{number}/confirm", (string id, int number, ConfirmRequest request) =>
{
    lock (store.Gate) { var p = store.Load(id); store.ConfirmCutout(p, p.Parts.Single(x => x.Number == number), request.Placement); return Results.Ok(p); }
});
app.MapPost("/api/projects/{id}/parts/{number}/keep", (string id, int number) =>
{
    lock (store.Gate) { var p = store.Load(id); store.Confirm(p, p.Parts.Single(x => x.Number == number), null); return Results.Ok(p); }
});
app.MapPost("/api/projects/{id}/parts/{number}/empty", (string id, int number) =>
{
    lock (store.Gate) { var p = store.Load(id); store.Confirm(p, p.Parts.Single(x => x.Number == number), null, empty: true); return Results.Ok(p); }
});
app.MapPost("/api/projects/{id}/parts/{number}/restore", (string id, int number, RestoreRequest request) =>
{
    lock (store.Gate) { var p = store.Load(id); store.Restore(p, p.Parts.Single(x => x.Number == number), request.Revision); return Results.Ok(p); }
});
app.MapPost("/api/projects/{id}/sam", async (string id, SamRequest request) =>
{
    ProjectState p; lock (store.Gate) p = store.Load(id);
    return Results.Bytes(await sam.Predict(p, request), "image/png");
});
app.MapPost("/api/projects/{id}/keep-pending", (string id) => {lock(store.Gate){var p=store.Load(id);store.KeepPending(p);return Results.Ok(p);}});
app.MapPost("/api/projects/{id}/export-tail", (string id,FinalizeExportRequest request)=>{lock(store.Gate)return Results.Ok(new CompactExport(store,exporter).Complete(store.Load(id),request));});
app.MapPost("/api/projects/{id}/preview", (string id) => { lock (store.Gate) return Results.Ok(exporter.Export(store.Load(id), false)); });
app.MapPost("/api/projects/{id}/export", (string id) => { lock (store.Gate) return Results.Ok(new CompactExport(store,exporter).Begin(store.Load(id))); });
app.MapGet("/api/projects/{id}/preview-model", (string id, bool? original,string? revision) =>
{
    lock (store.Gate)
    {
        var p = store.Load(id);
        if(revision is not null&&!p.Exports.Contains(revision))throw new InvalidDataException("模型不属于本项目导出");
        var snapshot = original == true ? "source" : revision ?? p.Exports.LastOrDefault() ?? throw new InvalidOperationException("请先生成整体预览素材或最终导出");
        var gameModel = File.Exists(ProjectStore.Under(store.DirectoryFor(id),snapshot+"/替换文件完成标记.txt"));
        var model = original == true ? "source/" : snapshot + (gameModel ? "/游戏图集核对/" : "/动画模型/");
        var atlasFile=p.AtlasFile;var skeletonFile=p.SkeletonFile;var previewVariant=p.Variant;
        var batchFile=ProjectStore.Under(store.DirectoryFor(id),snapshot+"/跨画质导出.json");
        if(gameModel&&File.Exists(batchFile))
        {
            var batch=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(batchFile))!;
            var companion=batch["Models"]!.AsArray().FirstOrDefault();
            if(companion is not null){model=snapshot+"/"+companion["Model"]!.GetValue<string>()+"/";atlasFile=companion["AtlasFile"]!.GetValue<string>();skeletonFile=companion["SkeletonFile"]!.GetValue<string>();previewVariant=companion["Variant"]!.GetValue<string>();}
        }
        string Url(string name) => "/files/" + id + "/" + string.Join('/', (model + name).Replace('\\','/').Split('/').Select(Uri.EscapeDataString)) + "?t=" + token;
        var atlas = Atlas.Parse(File.ReadAllText(ProjectStore.Under(store.DirectoryFor(id), model + atlasFile)));
        return Results.Ok(new { ApiVersion = 1, p.CardId, p.ChineseName, p.SpineVersion, RendererImplemented = true, Renderer = "Rigby2D", Experimental = true,
            SkeletonUrl = Url(skeletonFile), AtlasUrl = Url(atlasFile), GameReplacementLayout = gameModel, PreviewVariant=previewVariant,
            Pages = atlas.Pages.Select(page => new { Name = page.Key, Url = Url(page.Key), Pma = page.Value.GetValueOrDefault("pma", "false") == "true" }),
            Snapshot = snapshot });
    }
});
app.MapPost("/api/projects/{id}/open-export", (string id, RestoreRequest request) =>
{
    var p = store.Load(id);
    if (!p.Exports.Contains(request.Revision)) throw new InvalidDataException("目录不是本项目已生成的导出");
    Process.Start(new ProcessStartInfo(CompactExport.OpenFolder(store,p,request.Revision)) { UseShellExecute = true }); return Results.Ok();
});
app.MapPost("/api/quit", () => { if(updater.Running)throw new InvalidOperationException("请先停止素材更新，再关闭工具。");app.Lifetime.StopApplication(); return Results.Ok(); });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
JsonFile.Write(activeFile, new Dictionary<string, string> { ["Url"] = address, ["Token"] = token, ["Pid"] = Environment.ProcessId.ToString() });
File.WriteAllText(Path.Combine(AppEnvironment.Runtime(workspace),"打开工作台.url"),"[InternetShortcut]\r\nURL="+address+"#"+token+"\r\n");
Console.WriteLine("MD动画替换助手 1.5.6\n" + address + "#" + token + "\n仅监听本机。关闭服务请使用页面右上角的关闭工具。");
if (!args.Contains("--no-browser")) TryOpenBrowser(address + "#" + token);
await app.WaitForShutdownAsync();

static void TryOpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        // Opening a browser is optional; a missing association or a restricted launcher must not stop the project server.
        Console.Error.WriteLine("无法自动打开默认浏览器。服务继续运行，请用浏览器打开：" + url + "\n原因：" + e.Message);
        if(AppEnvironment.Portable(AppContext.BaseDirectory))NativeDialogs.Error("无法自动打开浏览器，服务已启动。请在浏览器打开：\n"+url+"\n也可双击运行数据目录中的“打开工作台.url”。");
    }
}
