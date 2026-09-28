using System.Text;
using System.Text.Json.Nodes;
using HaierWidget.Haier;
using HaierWidget.Util;

namespace HaierWidget.Widget;

/// <summary>某台设备的当前视图：设备信息 + 控件定义 + 属性值。</summary>
public sealed class DeviceState
{
    public required DeviceInfo Device { get; set; }
    public List<Control> Controls { get; set; } = new();
    public Dictionary<string, string> Values { get; } = new();
    public bool Online { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Error { get; set; }
    public string? Notice { get; set; }
}

/// <summary>
/// 进程内唯一的数据中枢：会话、设备列表、按型号缓存的属性定义、属性快照、网关推送、控制命令。
/// 所有小组件实例共用一个 Hub，避免每个卡片各连一条 WebSocket。
/// </summary>
public sealed class HaierHub
{
    public static HaierHub Instance { get; } = new();

    private static readonly TimeSpan DevicesTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(8);

    /// <summary>面板打开（Activate）触发的刷新：距上次刷新不足这个间隔就直接用缓存。</summary>
    public static readonly TimeSpan ActivateMinInterval = TimeSpan.FromSeconds(20);

    /// <summary>用户点“刷新”的最小间隔，防止连点。</summary>
    public static readonly TimeSpan ManualRefreshMinInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _states = new();
    private readonly Dictionary<string, JsonArray> _modelCache = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _gatewayLock = new(1, 1);
    private readonly HashSet<string> _subscribed = new();
    private FileSystemWatcher? _watcher;
    private HaierSession? _session;
    private List<DeviceInfo>? _devices;
    private DateTime _devicesAt;
    private HaierGateway? _gateway;
    private CancellationTokenSource? _gatewayCts;
    private int _gatewayFailures;
    private Task? _refreshAll;
    private DateTime _refreshedAt;

    /// <summary>设备状态变化（值更新 / 在线状态 / 错误）。参数为 deviceId。</summary>
    public event Action<string>? DeviceChanged;

    /// <summary>会话变化（登录 / 退出）。</summary>
    public event Action? SessionChanged;

    private HaierHub()
    {
        _session = HaierSession.Load(Paths.SessionFile);
        try
        {
            _watcher = new FileSystemWatcher(Paths.DataDir, "session.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => OnSessionFileChanged();
            _watcher.Created += (_, _) => OnSessionFileChanged();
            _watcher.Deleted += (_, _) => OnSessionFileChanged();
            _watcher.Renamed += (_, _) => OnSessionFileChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"无法监视会话文件: {ex.Message}");
        }
    }

    public bool LoggedIn => _session is not null;

    public string? Account => _session?.MaskedAccount;

    private void OnSessionFileChanged()
    {
        // 登录窗口写文件后再读；稍等一下避免读到半截
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            var fresh = HaierSession.Load(Paths.SessionFile);
            bool changed;
            lock (_gate)
            {
                changed = fresh?.Token != _session?.Token || fresh?.ClientId != _session?.ClientId;
                if (changed)
                {
                    _session = fresh;
                    _devices = null;
                    _states.Clear();
                    _refreshedAt = default;
                }
            }
            if (changed)
            {
                Log.Info(fresh is null ? "会话已清除" : $"会话已更新: {fresh.MaskedAccount}");
                await ResetGatewayAsync();
                SessionChanged?.Invoke();
            }
        });
    }

    public void Logout()
    {
        lock (_gate)
        {
            _session = null;
            _devices = null;
            _states.Clear();
            _refreshedAt = default;
        }
        try
        {
            File.Delete(Paths.SessionFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除会话文件失败: {ex.Message}");
        }
        _ = ResetGatewayAsync();
        SessionChanged?.Invoke();
    }

    // ---------------------------------------------------------------- 会话 / 设备

    private async Task<HaierApi> ApiAsync()
    {
        var session = _session ?? throw new HaierAuthException("尚未登录");
        await _sessionLock.WaitAsync();
        try
        {
            return await session.EnsureValidAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _devices is not null && DateTime.UtcNow - _devicesAt < DevicesTtl)
            {
                return _devices;
            }
        }
        var api = await ApiAsync();
        var devices = await api.GetDevicesAsync();
        lock (_gate)
        {
            _devices = devices;
            _devicesAt = DateTime.UtcNow;
            foreach (var d in devices)
            {
                if (_states.TryGetValue(d.Id, out var st))
                {
                    st.Device = d;
                    st.Online = d.Online;
                }
            }
        }
        Log.Info($"设备列表: {devices.Count} 台");
        return devices;
    }

    public DeviceState? TryGetState(string deviceId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(deviceId, out var st) ? st : null;
        }
    }

    /// <summary>最近一次全量刷新完成的本地时间；还没刷新过为 null。</summary>
    public DateTime? RefreshedAt
    {
        get
        {
            lock (_gate)
            {
                return _refreshedAt == default ? null : _refreshedAt.ToLocalTime();
            }
        }
    }

    /// <summary>按设备列表顺序返回当前已知的全部设备状态（不发请求）。</summary>
    public List<DeviceState> Snapshot()
    {
        lock (_gate)
        {
            var devices = _devices ?? new List<DeviceInfo>();
            return devices
                .Select(d => _states.TryGetValue(d.Id, out var st) ? st : new DeviceState { Device = d, Online = d.Online })
                .ToList();
        }
    }

    /// <summary>
    /// 加载全部设备：设备列表 + 一次批量数字模型快照 + 网关订阅。
    /// 距上次刷新不足 maxAge 时直接返回缓存；已有刷新在进行时不再发起新请求而是等它完成，
    /// 所以面板打开时多个小组件同时 Activate 也只会打一轮请求。
    /// </summary>
    public async Task<List<DeviceState>> LoadAllAsync(TimeSpan maxAge)
    {
        Task task;
        lock (_gate)
        {
            if (_refreshAll is null)
            {
                if (_states.Count > 0 && _devices is not null && DateTime.UtcNow - _refreshedAt < maxAge)
                {
                    return Snapshot();
                }
                // 手动刷新时连设备列表一起重新拉，其余情况沿用 5 分钟的列表缓存
                bool forceDevices = maxAge <= ManualRefreshMinInterval;
                task = _refreshAll = Task.Run(() => RefreshAllCoreAsync(forceDevices));
            }
            else
            {
                task = _refreshAll;
            }
        }
        await task;
        return Snapshot();
    }

    private async Task RefreshAllCoreAsync(bool forceDevices)
    {
        try
        {
            var devices = await GetDevicesAsync(force: forceDevices);
            bool newSubscription = false;
            lock (_gate)
            {
                foreach (var d in devices)
                {
                    if (!_states.ContainsKey(d.Id))
                    {
                        _states[d.Id] = new DeviceState { Device = d, Online = d.Online };
                    }
                    newSubscription |= _subscribed.Add(d.Id);
                }
            }
            await RefreshSnapshotAsync(devices.Select(d => d.Id));
            if (newSubscription || _gateway?.Connected != true)
            {
                _ = EnsureGatewayAsync();
            }
            Log.Info($"已刷新 {devices.Count} 台设备的状态");
        }
        finally
        {
            lock (_gate)
            {
                // 失败也记时间：错误已经显示在卡片上，靠“刷新”按钮（2 秒节流）重试即可
                _refreshedAt = DateTime.UtcNow;
                _refreshAll = null;
            }
        }
    }

    /// <summary>获取（必要时加载）单台设备状态：设备信息、控件定义、属性快照，并确保网关订阅。</summary>
    public async Task<DeviceState> LoadDeviceAsync(string deviceId, bool refresh = false)
    {
        var devices = await GetDevicesAsync();
        var device = devices.FirstOrDefault(d => d.Id == deviceId)
            ?? throw new HaierApiException("账号下找不到该设备，可能已被解绑");

        DeviceState state;
        bool needModel;
        lock (_gate)
        {
            if (!_states.TryGetValue(deviceId, out state!))
            {
                state = new DeviceState { Device = device, Online = device.Online };
                _states[deviceId] = state;
            }
            state.Device = device;
            state.Online = device.Online;
            needModel = refresh || state.Controls.Count == 0 || state.Values.Count == 0;
        }

        if (needModel)
        {
            await RefreshSnapshotAsync(new[] { deviceId });
        }
        _ = EnsureSubscribedAsync(deviceId);
        return state;
    }

    /// <summary>用 HTTP 拉一次数字模型，更新控件定义（合并型号缓存）与属性值。</summary>
    public async Task RefreshSnapshotAsync(IEnumerable<string> deviceIds)
    {
        var ids = deviceIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }
        var api = await ApiAsync();
        var models = await api.GetDigitalModelsAsync(ids);
        foreach (var id in ids)
        {
            DeviceState? state;
            lock (_gate)
            {
                _states.TryGetValue(id, out state);
            }
            if (state is null)
            {
                continue;
            }
            if (!models.TryGetValue(id, out var model))
            {
                state.Error = "云端未返回设备状态";
                DeviceChanged?.Invoke(id);
                continue;
            }
            var fresh = model["attributes"] as JsonArray ?? new JsonArray();
            var merged = UpdateModelCache(state.Device.ModelKey, fresh);
            var values = HaierApi.SnapshotOf(model);
            lock (_gate)
            {
                state.Controls = Capabilities.BuildControls(merged);
                foreach (var (k, v) in values)
                {
                    state.Values[k] = v;
                }
                state.UpdatedAt = DateTime.Now;
                state.Error = null;
            }
            DeviceChanged?.Invoke(id);
        }
    }

    private JsonArray UpdateModelCache(string modelKey, JsonArray fresh)
    {
        var path = Path.Combine(Paths.ModelsDir, SafeFileName(modelKey) + ".json");
        JsonArray? cached;
        lock (_gate)
        {
            if (!_modelCache.TryGetValue(modelKey, out cached))
            {
                cached = null;
                try
                {
                    if (File.Exists(path))
                    {
                        cached = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonArray;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"读取型号缓存失败 {path}: {ex.Message}");
                }
            }
        }
        var merged = Capabilities.MergeAttributes(cached, fresh);
        // 缓存只保留定义，不保留 value，避免旧值混进来
        var toStore = new JsonArray();
        foreach (var a in merged)
        {
            if (a is JsonObject o)
            {
                var clone = (JsonObject)o.DeepClone();
                clone.Remove("value");
                toStore.Add(clone);
            }
        }
        lock (_gate)
        {
            _modelCache[modelKey] = toStore;
        }
        try
        {
            File.WriteAllText(path, toStore.ToJsonString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入型号缓存失败 {path}: {ex.Message}");
        }
        return merged;
    }

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s;
    }

    // ---------------------------------------------------------------- 网关

    private async Task EnsureSubscribedAsync(string deviceId)
    {
        lock (_gate)
        {
            if (!_subscribed.Add(deviceId))
            {
                // 已订阅；只要连接还在就不用管
                if (_gateway?.Connected == true)
                {
                    return;
                }
            }
        }
        await EnsureGatewayAsync();
    }

    private async Task EnsureGatewayAsync()
    {
        if (_session is null)
        {
            return;
        }
        await _gatewayLock.WaitAsync();
        try
        {
            if (_gateway?.Connected == true)
            {
                await SubscribeAllAsync(_gateway);
                return;
            }
            await ConnectGatewayAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"网关连接失败: {ex.Message}");
            ScheduleReconnect();
        }
        finally
        {
            _gatewayLock.Release();
        }
    }

    private async Task ConnectGatewayAsync()
    {
        var api = await ApiAsync();
        var old = _gateway;
        _gateway = null;
        if (old is not null)
        {
            await old.CloseAsync();
        }
        _gatewayCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gatewayCts = cts;

        var gw = new HaierGateway(api);
        gw.DataReceived += OnGatewayData;
        gw.OnlineChanged += OnGatewayOnline;
        await gw.ConnectAsync(cts.Token);
        _gateway = gw;
        _gatewayFailures = 0;
        await SubscribeAllAsync(gw);
        Log.Info("网关已连接并订阅");

        _ = gw.Closed.ContinueWith(_ =>
        {
            if (ReferenceEquals(_gateway, gw) && !cts.IsCancellationRequested)
            {
                Log.Warn("网关连接断开，准备重连");
                _gateway = null;
                ScheduleReconnect();
            }
        }, TaskScheduler.Default);
    }

    private async Task SubscribeAllAsync(HaierGateway gw)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _subscribed.ToList();
        }
        if (ids.Count > 0)
        {
            await gw.SubscribeAsync(ids);
        }
    }

    private void ScheduleReconnect()
    {
        var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(_gatewayFailures, 4))));
        _gatewayFailures++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            bool any;
            lock (_gate)
            {
                any = _subscribed.Count > 0;
            }
            if (any && _session is not null)
            {
                await EnsureGatewayAsync();
            }
        });
    }

    private async Task ResetGatewayAsync()
    {
        _gatewayCts?.Cancel();
        var gw = _gateway;
        _gateway = null;
        if (gw is not null)
        {
            await gw.CloseAsync();
        }
        lock (_gate)
        {
            _subscribed.Clear();
        }
    }

    /// <summary>没有小组件在用时释放网关连接。</summary>
    public async Task ReleaseAsync()
    {
        await ResetGatewayAsync();
        lock (_gate)
        {
            _states.Clear();
            _refreshedAt = default;
        }
    }

    private void OnGatewayData(string deviceId, Dictionary<string, string> values)
    {
        DeviceState? state;
        lock (_gate)
        {
            if (!_states.TryGetValue(deviceId, out state))
            {
                return;
            }
            foreach (var (k, v) in values)
            {
                state.Values[k] = v;
            }
            state.UpdatedAt = DateTime.Now;
            state.Online = true;
            state.Error = null;
        }
        DeviceChanged?.Invoke(deviceId);
    }

    private void OnGatewayOnline(string deviceId, bool online)
    {
        DeviceState? state;
        lock (_gate)
        {
            if (!_states.TryGetValue(deviceId, out state))
            {
                return;
            }
            state.Online = online;
        }
        DeviceChanged?.Invoke(deviceId);
    }

    // ---------------------------------------------------------------- 控制

    /// <summary>下发控制命令：先乐观更新本地值，失败再回滚并用 HTTP 刷新。</summary>
    public async Task<string?> ControlAsync(string deviceId, string key, string value)
    {
        DeviceState? state;
        string? previous = null;
        lock (_gate)
        {
            _states.TryGetValue(deviceId, out state);
            if (state is not null)
            {
                state.Values.TryGetValue(key, out previous);
                state.Values[key] = value;
                state.Notice = null;
            }
        }
        DeviceChanged?.Invoke(deviceId);

        string? error = null;
        try
        {
            await EnsureGatewayAsync();
            var gw = _gateway ?? throw new HaierApiException("网关未连接");
            var result = await gw.ControlAsync(deviceId, new Dictionary<string, string> { [key] = value }, ControlTimeout);
            if (!result.Ok)
            {
                error = result.ErrNo is null ? "设备未响应（超时）" : $"设备拒绝了命令 (errNo {result.ErrNo})";
            }
            else
            {
                Log.Info($"控制成功 {deviceId} {key}={value}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"控制失败 {deviceId} {key}={value}", ex);
            error = ex.Message;
        }

        if (error is not null && state is not null)
        {
            lock (_gate)
            {
                if (previous is null)
                {
                    state.Values.Remove(key);
                }
                else
                {
                    state.Values[key] = previous;
                }
                state.Notice = error;
            }
            DeviceChanged?.Invoke(deviceId);
            _ = Task.Run(async () =>
            {
                try { await RefreshSnapshotAsync(new[] { deviceId }); } catch { }
            });
        }
        return error;
    }
}
