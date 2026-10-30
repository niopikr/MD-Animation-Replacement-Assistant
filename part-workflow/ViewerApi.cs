using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PartWorkflow;

public sealed record ViewerRequest(string Command, int Slot, string Snapshot, int Port = 10086, string Motion = "idle");

public static class ViewerApi
{
    public static object Payload(string command, int slot, string config, string motion = "idle") => command switch
    {
        "load" => new { msg = 13000, msgId = 1, data = (object)new { id = slot - 1, file = config } },
        "play" => new { msg = 13200, msgId = 1, data = (object)new { id = slot - 1, type = 0, mtn = motion } },
        "status" => new { msg = 10000, msgId = 1, data = (object)new { } },
        _ => throw new InvalidDataException("未知预览指令")
    };
    public static async Task<object> Send(ProjectStore store, string id, ViewerRequest request)
    {
        if (request.Slot < 1 || request.Slot > 8 || request.Port < 1024 || request.Port > 65535) throw new InvalidDataException("插槽或端口无效");
        var project = store.Load(id);
        string config = "";
        if (request.Command != "status")
        {
            if (!project.Exports.Contains(request.Snapshot)) throw new InvalidDataException("先生成本项目的预览快照");
            config = ProjectStore.Under(store.DirectoryFor(id), request.Snapshot + "/动画模型/预览.config.json");
            if (!File.Exists(config)) throw new FileNotFoundException("预览配置缺失");
        }
        using var connection = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{request.Port}/api"), timeout.Token);
            if (request.Command == "play")
            {
                using var configJson = JsonDocument.Parse(File.ReadAllText(config));
                var group = request.Motion.Split(':')[0];
                if (string.IsNullOrWhiteSpace(group) || !configJson.RootElement.TryGetProperty("motions", out var motions) || !motions.TryGetProperty(group, out _))
                    return new { Connected = true, Message = "此配置未定义该动作组。先在 EX Studio 为骨骼 animation 建立 idle 等动作组并保存，再调用播放；接口已连接，但未发送无效动作。" };
            }
            var payload = JsonSerializer.Serialize(Payload(request.Command, request.Slot, config, request.Motion));
            await connection.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[65536];
            using var reply = new MemoryStream();
            while (true)
            {
                var received = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("播放器关闭了接口连接");
                reply.Write(buffer, 0, received.Count);
                if (reply.Length > 65536) throw new IOException("接口回复过大");
                if (received.EndOfMessage) break;
            }
            var response = Encoding.UTF8.GetString(reply.ToArray());
            using var parsed = JsonDocument.Parse(response);
            var error = parsed.RootElement.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null;
            return new { Connected = true, Command = request.Command, Response = response,
                Message = error ? "播放器返回错误：" + response : request.Command == "status" ? "已连接 ExAPI（尚未验证模型加载）。" : "播放器已回复。请查看 Live2DViewerEX 画面确认加载/动作效果。回复：" + response };
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            return new { Connected = false, Message = "未完成 ExAPI 通信。请启动 Live2DViewerEX 主程序，检查 ExAPI 服务与端口。原因：" + e.Message };
        }
    }
}
