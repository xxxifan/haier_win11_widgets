using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HaierWidget.Util;

namespace HaierWidget.Haier;

public sealed record GatewayResult(bool Ok, string? ErrNo, string? Raw);

/// <summary>海尔 WebSocket 设备网关（移植自 haier/gateway.py）。</summary>
public sealed class HaierGateway : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private readonly HaierApi _api;
    private readonly string _agClientId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Action<string, Dictionary<string, string>>? DataReceived;
    public event Action<string, bool>? OnlineChanged;

    public HaierGateway(HaierApi api)
    {
        _api = api;
        _agClientId = api.Token;
    }

    public bool Connected => _ws?.State == WebSocketState.Open;

    /// <summary>连接结束（主动关闭或异常断开）时完成。</summary>
    public Task Closed => _closed.Task;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var server = await _api.GetGatewayAsync();
        var url = $"{server}/userag?token={_api.Token}&agClientId={_agClientId}";
        Log.Info($"连接设备网关: {server}");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new HaierApiException($"连接网关失败: {ex.Message}", null, ex);
        }
        _ws = ws;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReaderLoopAsync(ws, _cts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public async Task CloseAsync()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        var ws = _ws;
        _ws = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch
            {
                // 忽略关闭期间的异常
            }
            ws.Dispose();
        }
        FailPending("网关已关闭");
        _closed.TrySetResult();
        cts?.Dispose();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    // ---------------------------------------------------------------- 发送

    public Task SubscribeAsync(IEnumerable<string> deviceIds)
    {
        var devs = new JsonArray();
        foreach (var id in deviceIds)
        {
            devs.Add(id);
        }
        return SendAsync(new JsonObject
        {
            ["agClientId"] = _agClientId,
            ["topic"] = "BoundDevs",
            ["content"] = new JsonObject { ["devs"] = devs },
        });
    }

    public Task HeartbeatAsync() => SendAsync(new JsonObject
    {
        ["agClientId"] = _agClientId,
        ["topic"] = "HeartBeat",
        ["content"] = new JsonObject { ["sn"] = RandomStr(32), ["duration"] = 0 },
    });

    public static JsonObject BuildBatchCmd(string agClientId, string deviceId, IReadOnlyDictionary<string, string> attributes, string? sn = null)
    {
        sn ??= RandomStr(32);
        var cmdArgs = new JsonObject();
        foreach (var (k, v) in attributes)
        {
            cmdArgs[k] = v;
        }
        return new JsonObject
        {
            ["agClientId"] = agClientId,
            ["topic"] = "BatchCmdReq",
            ["content"] = new JsonObject
            {
                ["trace"] = RandomStr(32),
                ["sn"] = sn,
                ["data"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["sn"] = sn,
                        ["index"] = 0,
                        ["delaySeconds"] = 0,
                        ["subSn"] = sn + ":0",
                        ["deviceId"] = deviceId,
                        ["cmdArgs"] = cmdArgs,
                    },
                },
            },
        };
    }

    /// <summary>发送控制命令并等待网关对该 sn 的响应；超时返回 Ok=false, ErrNo=null。</summary>
    public async Task<GatewayResult> ControlAsync(string deviceId, IReadOnlyDictionary<string, string> attributes, TimeSpan timeout)
    {
        var sn = RandomStr(32);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sn] = tcs;
        try
        {
            await SendAsync(BuildBatchCmd(_agClientId, deviceId, attributes, sn));
            using var cts = new CancellationTokenSource(timeout);
            var resp = await tcs.Task.WaitAsync(cts.Token);
            var errNo = Json.Str(resp["content"]?["errNo"]);
            bool ok = errNo is null or "" or "0" or "None";
            return new GatewayResult(ok, errNo, resp.ToJsonString());
        }
        catch (OperationCanceledException)
        {
            return new GatewayResult(false, null, null);
        }
        finally
        {
            _pending.TryRemove(sn, out _);
        }
    }

    private async Task SendAsync(JsonObject msg)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            throw new HaierApiException("网关未连接");
        }
        var text = msg.ToJsonString(HaierApi.CompactJson);
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---------------------------------------------------------------- 接收

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                try
                {
                    await HeartbeatAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn($"发送心跳失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReaderLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Info($"网关关闭连接: {result.CloseStatus} {result.CloseStatusDescription}");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                try
                {
                    HandleMessage(text);
                }
                catch (Exception ex)
                {
                    Log.Error($"处理网关消息失败: {text}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"网关连接异常断开: {ex.Message}");
        }
        finally
        {
            FailPending("网关连接已断开");
            _closed.TrySetResult();
        }
    }

    private void HandleMessage(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject msg)
        {
            return;
        }
        var topic = Json.Str(msg["topic"]);
        var content = msg["content"] as JsonObject;

        if (content is not null && topic != "BatchCmdReq")
        {
            var sns = new HashSet<string>();
            if (Json.Str(content["sn"]) is { Length: > 0 } sn1)
            {
                sns.Add(sn1);
            }
            if (content["data"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject o && Json.Str(o["sn"]) is { Length: > 0 } sn2)
                    {
                        sns.Add(sn2);
                    }
                }
            }
            foreach (var sn in sns)
            {
                if (_pending.TryGetValue(sn, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
            }
        }

        if (topic != "GenMsgDown" || content is null)
        {
            return;
        }
        var businType = Json.Str(content["businType"]);
        var dataB64 = Json.Str(content["data"]);
        if (string.IsNullOrEmpty(dataB64))
        {
            return;
        }
        if (JsonNode.Parse(Convert.FromBase64String(dataB64)) is not JsonObject data)
        {
            return;
        }

        if (businType == "DigitalModel")
        {
            var deviceId = Json.Str(data["dev"]) ?? "";
            var args = Json.Str(data["args"]) ?? "";
            var values = DecodeDigitalModel(args);
            DataReceived?.Invoke(deviceId, values);
        }
        else if (businType is "DevOnlineNotify" or "DevOfflineNotify")
        {
            bool online = businType == "DevOnlineNotify";
            if (data["devs"] is JsonArray devs)
            {
                foreach (var d in devs)
                {
                    var id = Json.Str(d);
                    if (!string.IsNullOrEmpty(id))
                    {
                        Log.Info($"设备 {id} {(online ? "上线" : "离线")}");
                        OnlineChanged?.Invoke(id, online);
                    }
                }
            }
        }
    }

    /// <summary>解码 DigitalModel 的 args（base64 -> gzip -> JSON），返回 {name: value}。</summary>
    public static Dictionary<string, string> DecodeDigitalModel(string args)
    {
        using var input = new MemoryStream(Convert.FromBase64String(args));
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var values = new Dictionary<string, string>();
        if (JsonNode.Parse(json) is JsonObject model)
        {
            return HaierApi.SnapshotOf(model);
        }
        return values;
    }

    private void FailPending(string reason)
    {
        foreach (var (sn, tcs) in _pending)
        {
            tcs.TrySetException(new HaierApiException(reason));
            _pending.TryRemove(sn, out _);
        }
    }

    private static string RandomStr(int length)
    {
        const string alphabet = "abcdef1234567890";
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }
        return new string(chars);
    }
}
