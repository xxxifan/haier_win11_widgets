using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HaierWidget.Util;

namespace HaierWidget.Haier;

/// <summary>登录会话，文件格式与 Python 版 session.json 完全一致，可以互相复用。</summary>
public sealed class HaierSession
{
    private const long RefreshAheadSeconds = 86400;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("app_source")] public string AppSource { get; set; } = HaierApi.SourceApp;
    [JsonPropertyName("user")] public JsonObject? User { get; set; }

    [JsonIgnore] public string? Path { get; set; }

    [JsonIgnore] public long ExpiresIn => ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonIgnore]
    public string MaskedAccount
    {
        get
        {
            var mobile = Json.Str(User?["mobile"]) ?? ClientId;
            return mobile.Length >= 11 ? mobile[..3] + "****" + mobile[^4..] : mobile;
        }
    }

    public static HaierSession? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var session = JsonSerializer.Deserialize<HaierSession>(File.ReadAllText(path, Encoding.UTF8), FileJson);
            if (session is null || string.IsNullOrEmpty(session.Token) || string.IsNullOrEmpty(session.RefreshToken))
            {
                return null;
            }
            session.Path = path;
            return session;
        }
        catch (Exception ex)
        {
            Log.Error($"读取会话失败 {path}", ex);
            return null;
        }
    }

    public void Save(string? path = null)
    {
        path ??= Path ?? Paths.SessionFile;
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var text = JsonSerializer.Serialize(this, FileJson).Replace("\r\n", "\n");
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public HaierApi Client() => new(ClientId, Token, AppSource);

    public static async Task<HaierSession> LoginAsync(string phone, string password, string? path = null)
    {
        var api = new HaierApi(phone, "", HaierApi.SourceApp);
        var info = await api.PhoneLoginAsync(phone, password);
        api.Token = info.Token;
        var user = await api.GetUserInfoAsync();
        var session = new HaierSession
        {
            ClientId = phone,
            Token = info.Token,
            RefreshToken = info.RefreshToken,
            ExpiresAt = info.ExpiresAt,
            AppSource = HaierApi.SourceApp,
            User = new JsonObject
            {
                ["userId"] = Json.Str(user["userId"]),
                ["mobile"] = Json.Str(user["mobile"]),
                ["username"] = Json.Str(user["username"]),
            },
            Path = path,
        };
        session.Save();
        return session;
    }

    public async Task RefreshAsync()
    {
        var api = new HaierApi(ClientId, "", AppSource);
        var info = await api.RefreshTokenAsync(RefreshToken);
        Token = info.Token;
        RefreshToken = info.RefreshToken;
        ExpiresAt = info.ExpiresAt;
        Save();
        Log.Info($"token 已刷新，有效期至 {DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>返回可用的客户端；临近过期（或校验失败）时自动刷新。</summary>
    public async Task<HaierApi> EnsureValidAsync(bool forceCheck = false)
    {
        bool needRefresh = ExpiresIn < RefreshAheadSeconds;
        if (!needRefresh && forceCheck)
        {
            try
            {
                await Client().GetUserInfoAsync();
            }
            catch (HaierAuthException)
            {
                needRefresh = true;
            }
        }
        if (needRefresh)
        {
            await RefreshAsync();
        }
        return Client();
    }
}
