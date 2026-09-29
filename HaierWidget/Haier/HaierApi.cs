using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaierWidget.Util;

namespace HaierWidget.Haier;

public class HaierApiException : Exception
{
    public string? RetCode { get; }

    public HaierApiException(string message, string? retCode = null, Exception? inner = null) : base(message, inner)
    {
        RetCode = retCode;
    }
}

public sealed class HaierAuthException : HaierApiException
{
    public HaierAuthException(string message, string? retCode = null, Exception? inner = null) : base(message, retCode, inner) { }
}

public sealed record TokenInfo(string Token, string RefreshToken, long ExpiresAt);

public sealed class DeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Type { get; init; }
    public string? ProductCode { get; init; }
    public string? ProductName { get; init; }
    public string? WifiType { get; init; }
    public string? Room { get; init; }
    public bool Online { get; init; }

    /// <summary>按型号缓存属性定义用的键。</summary>
    public string ModelKey => $"{ProductCode ?? "unknown"}_{WifiType ?? "unknown"}";

    public static DeviceInfo From(JsonObject raw)
    {
        var id = Json.Str(raw["deviceId"]) ?? "";
        return new DeviceInfo
        {
            Id = id,
            Name = Json.Str(raw["deviceName"]) is { Length: > 0 } n ? n : id,
            Type = Json.Str(raw["deviceType"]),
            ProductCode = Json.Str(raw["productCodeT"]),
            ProductName = Json.Str(raw["productNameT"]),
            WifiType = Json.Str(raw["wifiType"]),
            Room = Json.Str(raw["roomName"]),
            Online = Json.Bool(raw["online"]),
        };
    }
}

/// <summary>海尔智家 HTTP 接口（移植自 haier/client.py）。</summary>
public sealed class HaierApi
{
    public const string SourceApp = "app";
    public const string SourceWxapp = "wxapp";

    private static readonly Dictionary<string, (string AppId, string AppKey)> Sources = new()
    {
        [SourceApp] = ("MB-UZHSH-0001", "5dfca8714eb26e3a776e58a8273c8752"),
        [SourceWxapp] = ("MB-SHEZJAPPWXXCX-0000", "79ce99cc7f9804663939676031b8a427"),
    };

    private const string LoginApi = "https://zj.haier.net/api-gw/oauthserver/account/v1/login";
    private const string RefreshApi = "https://zj.haier.net/api-gw/oauthserver/account/v1/refreshToken";
    private const string UserInfoApi = "https://account-api.haier.net/v2/haier/userinfo";
    private const string DevicesApi = "https://uws.haier.net/uds/v1/protected/deviceinfos";
    private const string DigitalModelApi = "https://uws.haier.net/shadow/v1/devdigitalmodels";
    private const string GatewayApi = "https://uws.haier.net/gmsWS/wsag/assign";
    private const string SuccessCode = "00000";
    private const int MaxRetries = 3;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private readonly string _appId;
    private readonly string _appKey;

    public string ClientId { get; }
    public string Token { get; set; }
    public string AppSource { get; }

    public HaierApi(string clientId, string token, string appSource = SourceApp)
    {
        if (!Sources.TryGetValue(appSource, out var pair))
        {
            throw new ArgumentException($"未知的 app_source: {appSource}", nameof(appSource));
        }
        ClientId = clientId;
        Token = token;
        AppSource = appSource;
        (_appId, _appKey) = pair;
    }

    // ---------------------------------------------------------------- 签名

    public static string Sign(string appId, string appKey, string timestamp, string body, string url)
    {
        var compact = body.Replace("\t", "").Replace("\r", "").Replace("\n", "").Replace(" ", "");
        var path = new Uri(url).AbsolutePath;
        var content = path + compact + appId + appKey + timestamp;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string SequenceId() =>
        DateTime.Now.ToString("yyyyMMddHHmmss") + Random.Shared.Next(100000, 1000000).ToString();

    public Dictionary<string, string> CommonHeaders(string api, string body = "")
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        return new Dictionary<string, string>
        {
            ["accessToken"] = Token,
            ["appId"] = _appId,
            ["appKey"] = _appKey,
            ["clientId"] = ClientId,
            ["sequenceId"] = SequenceId(),
            ["sign"] = Sign(_appId, _appKey, timestamp, body, api),
            ["timestamp"] = timestamp,
            ["timezone"] = "+8",
            ["language"] = "zh-CN",
        };
    }

    // ---------------------------------------------------------------- 认证

    public async Task<TokenInfo> PhoneLoginAsync(string phone, string password)
    {
        var payload = new JsonObject { ["username"] = phone, ["password"] = password };
        var content = await PostJsonAsync(LoginApi, payload, authError: true);
        return ParseToken(content);
    }

    public async Task<TokenInfo> RefreshTokenAsync(string refreshToken)
    {
        var payload = new JsonObject { ["refreshToken"] = refreshToken };
        var content = await PostJsonAsync(RefreshApi, payload, authError: true);
        return ParseToken(content);
    }

    public Task<JsonObject> GetUserInfoAsync() => RetryAsync(async () =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoApi);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonObject? node = null;
        try { node = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (node is null || node.ContainsKey("error_description") || !node.ContainsKey("userId"))
        {
            var desc = node?["error_description"]?.ToString() ?? Truncate(text);
            throw new HaierAuthException($"获取用户信息失败: {desc}");
        }
        return node;
    });

    // ---------------------------------------------------------------- 设备

    public async Task<List<DeviceInfo>> GetDevicesAsync()
    {
        var content = await GetJsonAsync(DevicesApi);
        var list = new List<DeviceInfo>();
        if (content["deviceinfos"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject o && Json.Str(o["deviceId"]) is { Length: > 0 })
                {
                    list.Add(DeviceInfo.From(o));
                }
            }
        }
        // 云端每次返回的顺序都可能不同，这里固定按名称（同名再按 deviceId）排序，
        // 保证刷新后设备列表和小组件卡片不会换位置。
        list.Sort(static (a, b) =>
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
        });
        return list;
    }

    /// <summary>批量获取数字模型，返回 deviceId 到模型对象（含 attributes）。</summary>
    /// <summary>
    /// 一次数字模型请求带几台设备。实测云端只接受 1 台（多台返回 B00005 参数数量超出可接收范围），
    /// 多台时并发逐台请求；若将来放宽上限可调大，超限仍会自动对半拆分重试。
    /// </summary>
    private const int DigitalModelBatch = 1;

    public async Task<Dictionary<string, JsonObject>> GetDigitalModelsAsync(IEnumerable<string> deviceIds)
    {
        var ids = deviceIds.Distinct().ToList();
        var result = new Dictionary<string, JsonObject>();
        if (ids.Count == 0)
        {
            return result;
        }
        var parts = await Task.WhenAll(ids.Chunk(DigitalModelBatch).Select(c => FetchDigitalModelsAsync(c.ToList())));
        foreach (var part in parts)
        {
            foreach (var (k, v) in part)
            {
                result[k] = v;
            }
        }
        return result;
    }

    private async Task<Dictionary<string, JsonObject>> FetchDigitalModelsAsync(List<string> ids)
    {
        try
        {
            return await FetchDigitalModelsOnceAsync(ids);
        }
        catch (HaierApiException ex) when (ids.Count > 1 && ex.RetCode == "B00005")
        {
            Log.Warn($"数字模型一次请求 {ids.Count} 台超限，拆分重试");
            int half = ids.Count / 2;
            var parts = await Task.WhenAll(
                FetchDigitalModelsAsync(ids.Take(half).ToList()),
                FetchDigitalModelsAsync(ids.Skip(half).ToList()));
            var merged = new Dictionary<string, JsonObject>();
            foreach (var part in parts)
            {
                foreach (var (k, v) in part)
                {
                    merged[k] = v;
                }
            }
            return merged;
        }
    }

    private async Task<Dictionary<string, JsonObject>> FetchDigitalModelsOnceAsync(List<string> ids)
    {
        var result = new Dictionary<string, JsonObject>();
        var infoList = new JsonArray();
        foreach (var id in ids)
        {
            infoList.Add(new JsonObject { ["deviceId"] = id });
        }
        var content = await PostJsonAsync(DigitalModelApi, new JsonObject { ["deviceInfoList"] = infoList });
        if (content["detailInfo"] is not JsonObject detail)
        {
            return result;
        }
        foreach (var id in ids)
        {
            var node = detail[id];
            JsonObject? model = null;
            try
            {
                model = node switch
                {
                    JsonObject o => o,
                    JsonValue v when v.TryGetValue<string>(out var s) => JsonNode.Parse(s) as JsonObject,
                    _ => null,
                };
            }
            catch (JsonException) { }
            if (model is not null)
            {
                result[id] = model;
            }
            else
            {
                Log.Warn($"设备 {id} 未返回数字模型");
            }
        }
        return result;
    }

    /// <summary>取属性快照：{name: value}。</summary>
    public static Dictionary<string, string> SnapshotOf(JsonObject model)
    {
        var values = new Dictionary<string, string>();
        if (model["attributes"] is JsonArray attrs)
        {
            foreach (var a in attrs)
            {
                if (a is JsonObject o && Json.Str(o["name"]) is { Length: > 0 } name && o.ContainsKey("value"))
                {
                    values[name] = Json.Str(o["value"]) ?? "";
                }
            }
        }
        return values;
    }

    public async Task<string> GetGatewayAsync()
    {
        var payload = new JsonObject { ["clientId"] = ClientId, ["token"] = Token };
        var content = await PostJsonAsync(GatewayApi, payload);
        var addr = Json.Str(content["agAddr"]) ?? throw new HaierApiException("网关分配响应缺少 agAddr");
        return addr.Replace("http://", "wss://").Replace("https://", "wss://");
    }

    // ---------------------------------------------------------------- 内部

    private Task<JsonObject> PostJsonAsync(string api, JsonObject payload, bool authError = false)
    {
        // 签名基于去空白后的 body，所以发送的 body 必须是同一串字符
        var body = payload.ToJsonString(CompactJson);
        return RetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, api);
            foreach (var (k, v) in CommonHeaders(api, body))
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return AssertSuccess(text, authError);
        });
    }

    private Task<JsonObject> GetJsonAsync(string api) => RetryAsync(async () =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        foreach (var (k, v) in CommonHeaders(api))
        {
            req.Headers.TryAddWithoutValidation(k, v);
        }
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return AssertSuccess(text, false);
    });

    private static async Task<T> RetryAsync<T>(Func<Task<T>> func)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await func();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                Log.Warn($"请求失败 {ex.GetType().Name}，第 {attempt}/{MaxRetries} 次: {ex.Message}");
                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 5)));
                }
            }
        }
        throw new HaierApiException($"请求失败，已重试 {MaxRetries} 次: {last?.Message}", null, last);
    }

    private static JsonObject AssertSuccess(string text, bool authError)
    {
        JsonObject? content;
        try
        {
            content = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            content = null;
        }
        if (content is null)
        {
            throw new HaierApiException($"接口返回了非 JSON 对象: {Truncate(text)}");
        }
        var retCode = Json.Str(content["retCode"]);
        if (retCode is not null && retCode != SuccessCode)
        {
            var msg = $"接口返回异常 [{retCode}]: {Json.Str(content["retInfo"])}";
            if (authError)
            {
                throw new HaierAuthException(msg, retCode);
            }
            throw new HaierApiException(msg, retCode);
        }
        return content;
    }

    private static TokenInfo ParseToken(JsonObject content)
    {
        var info = content["data"]?["tokenInfo"] as JsonObject
            ?? throw new HaierAuthException($"登录响应缺少 tokenInfo: {Truncate(content.ToJsonString())}");
        var token = Json.Str(info["accountToken"]) ?? "";
        var refresh = Json.Str(info["refreshToken"]) ?? "";
        var expiresIn = Json.Long(info["expiresIn"]);
        return new TokenInfo(token, refresh, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn);
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}

/// <summary>JsonNode 取值小工具：海尔接口里数字、布尔经常以字符串形式出现。</summary>
public static class Json
{
    public static string? Str(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                return s;
            }
            if (v.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }
            return v.ToJsonString();
        }
        return node.ToJsonString();
    }

    public static bool Bool(JsonNode? node)
    {
        var s = Str(node);
        return s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");
    }

    public static long Long(JsonNode? node)
    {
        var s = Str(node);
        return long.TryParse(s, out var n) ? n : 0;
    }

    public static double? Double(JsonNode? node)
    {
        var s = Str(node);
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
