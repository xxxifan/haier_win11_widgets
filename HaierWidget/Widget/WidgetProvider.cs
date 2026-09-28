using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HaierWidget.Haier;
using HaierWidget.Util;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace HaierWidget.Widget;

/// <summary>
/// 小组件提供程序。每个小组件实例都显示账号下全部设备（总览卡），
/// 只记住当前展开的设备（存进 CustomState，进程重启后可恢复）。
/// 数据统一由 HaierHub 提供；面板打开时的刷新在 Hub 里做了节流和合并。
/// </summary>
[ComVisible(true)]
[Guid(ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    public const string ClsidString = "7C1B3A9E-6E2D-4C5A-9B1F-2D8E4F0A6C11";

    private sealed class WidgetState
    {
        public required string Id { get; init; }
        public string ExpandedId { get; set; } = "";
        public string Size { get; set; } = CardBuilder.SizeMedium;
        public bool IsActive { get; set; }
        public bool ShowSettings { get; set; }
        public string? LastTemplate { get; set; }
    }

    private static readonly ConcurrentDictionary<string, WidgetState> Widgets = new();
    private static readonly ManualResetEvent NoWidgetsEvent = new(false);
    private static readonly object RenderGate = new();
    private static System.Threading.Timer? _renderTimer;
    private static int _initialized;

    public static WaitHandle NoWidgets => NoWidgetsEvent;

    public static int ActiveCount => Widgets.Values.Count(w => w.IsActive);

    public WidgetProvider()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            RecoverWidgets();
            HaierHub.Instance.DeviceChanged += OnDeviceChanged;
            HaierHub.Instance.SessionChanged += OnSessionChanged;
        }
    }

    /// <summary>进程启动时从 WidgetManager 找回已存在的小组件（CustomState 里存着展开的设备）。</summary>
    private static void RecoverWidgets()
    {
        try
        {
            var infos = WidgetManager.GetDefault().GetWidgetInfos() ?? Array.Empty<WidgetInfo>();
            foreach (var info in infos)
            {
                var ctx = info.WidgetContext;
                Widgets[ctx.Id] = new WidgetState
                {
                    Id = ctx.Id,
                    ExpandedId = info.CustomState ?? "",
                    Size = SizeName(ctx.Size),
                    IsActive = ctx.IsActive,
                };
            }
            Log.Info($"恢复了 {Widgets.Count} 个小组件");
        }
        catch (Exception ex)
        {
            Log.Error("恢复小组件失败", ex);
        }
        if (Widgets.IsEmpty)
        {
            NoWidgetsEvent.Set();
        }
    }

    private static string SizeName(WidgetSize size) => size switch
    {
        WidgetSize.Small => CardBuilder.SizeSmall,
        WidgetSize.Large => CardBuilder.SizeLarge,
        _ => CardBuilder.SizeMedium,
    };

    private static WidgetState Track(WidgetContext ctx)
    {
        var state = Widgets.GetOrAdd(ctx.Id, id => new WidgetState { Id = id });
        state.Size = SizeName(ctx.Size);
        NoWidgetsEvent.Reset();
        return state;
    }

    // ---------------------------------------------------------------- IWidgetProvider

    public void CreateWidget(WidgetContext widgetContext)
    {
        Log.Info($"CreateWidget {widgetContext.Id} def={widgetContext.DefinitionId} size={widgetContext.Size}");
        var state = Track(widgetContext);
        state.IsActive = true;
        _ = RenderAsync(state, HaierHub.ActivateMinInterval);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        Log.Info($"DeleteWidget {widgetId}");
        Widgets.TryRemove(widgetId, out _);
        if (Widgets.IsEmpty)
        {
            _ = HaierHub.Instance.ReleaseAsync();
            NoWidgetsEvent.Set();
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        Log.Info($"OnActionInvoked {args.WidgetContext.Id} verb={args.Verb} data={args.Data}");
        var state = Track(args.WidgetContext);
        state.IsActive = true;
        JsonObject data;
        try
        {
            data = JsonNode.Parse(string.IsNullOrWhiteSpace(args.Data) ? "{}" : args.Data) as JsonObject ?? new JsonObject();
        }
        catch
        {
            data = new JsonObject();
        }
        _ = HandleActionAsync(state, args.Verb, data);
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        var ctx = args.WidgetContext;
        Log.Info($"OnWidgetContextChanged {ctx.Id} size={ctx.Size}");
        var state = Track(ctx);
        state.IsActive = ctx.IsActive;
        _ = RenderAsync(state, null);
    }

    public void Activate(WidgetContext widgetContext)
    {
        Log.Info($"Activate {widgetContext.Id}");
        var state = Track(widgetContext);
        state.IsActive = true;
        // 面板重新可见：拉一次最新状态，但 20 秒内重复打开只用缓存（Hub 里节流 + 合并并发请求）
        _ = RenderAsync(state, HaierHub.ActivateMinInterval);
    }

    public void Deactivate(string widgetId)
    {
        Log.Info($"Deactivate {widgetId}");
        if (Widgets.TryGetValue(widgetId, out var state))
        {
            state.IsActive = false;
        }
    }

    // ---------------------------------------------------------------- IWidgetProvider2

    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs args)
    {
        Log.Info($"OnCustomizationRequested {args.WidgetContext.Id}");
        var state = Track(args.WidgetContext);
        state.ShowSettings = true;
        _ = RenderAsync(state, null);
    }

    // ---------------------------------------------------------------- 逻辑

    private async Task HandleActionAsync(WidgetState state, string verb, JsonObject data)
    {
        var hub = HaierHub.Instance;
        try
        {
            switch (verb)
            {
                case CardBuilder.VerbLogin:
                    await Windows.System.Launcher.LaunchUriAsync(new Uri("haierwidget://login"));
                    break;

                case CardBuilder.VerbLogout:
                    hub.Logout();
                    break;

                case CardBuilder.VerbSettings:
                    state.ShowSettings = true;
                    await RenderAsync(state, null);
                    break;

                case CardBuilder.VerbBack:
                    state.ShowSettings = false;
                    state.ExpandedId = "";
                    await RenderAsync(state, null);
                    break;

                case CardBuilder.VerbExpand:
                {
                    var id = Json.Str(data["deviceId"]) ?? "";
                    state.ExpandedId = state.ExpandedId == id ? "" : id;
                    state.ShowSettings = false;
                    await RenderAsync(state, null);
                    break;
                }

                case CardBuilder.VerbRefresh:
                    state.ShowSettings = false;
                    await RenderAsync(state, HaierHub.ManualRefreshMinInterval);
                    break;

                case CardBuilder.VerbSet:
                {
                    var deviceId = Json.Str(data["deviceId"]);
                    var key = Json.Str(data["key"]);
                    var value = Json.Str(data["value"]);
                    if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(key) || value is null)
                    {
                        break;
                    }
                    if (hub.TryGetState(deviceId) is null)
                    {
                        await hub.LoadAllAsync(TimeSpan.MaxValue);
                    }
                    // 结果通过 DeviceChanged 事件刷新到所有小组件
                    await hub.ControlAsync(deviceId, key, value);
                    break;
                }

                default:
                    Log.Warn($"未知 verb: {verb}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"处理动作 {verb} 失败", ex);
            Update(state, CardBuilder.MessageCard("操作失败", ex.Message, error: true));
        }
    }

    /// <summary>
    /// 渲染一个小组件。maxAge 为 null 表示只用缓存（没有缓存才请求），
    /// 否则距上次刷新超过 maxAge 就重新拉一次全部设备。
    /// </summary>
    private static async Task RenderAsync(WidgetState state, TimeSpan? maxAge)
    {
        var hub = HaierHub.Instance;
        var cached = hub.Snapshot();
        try
        {
            if (!hub.LoggedIn)
            {
                Update(state, CardBuilder.LoginCard(state.Size));
                return;
            }
            if (state.ShowSettings)
            {
                Update(state, CardBuilder.SettingsCard(hub.Account, cached.Count, hub.RefreshedAt));
                return;
            }
            // 先用缓存立刻画一帧（有则显示旧数据，无则显示加载中）
            Update(state, cached.Count > 0 ? BuildCard(state, cached, null) : CardBuilder.LoadingCard());
            var fresh = await hub.LoadAllAsync(maxAge ?? TimeSpan.MaxValue);
            Update(state, BuildCard(state, fresh, null));
        }
        catch (HaierAuthException ex)
        {
            Log.Error("会话失效", ex);
            Update(state, CardBuilder.MessageCard("登录已失效", ex.Message + "，请重新登录", error: true, retryVerb: CardBuilder.VerbLogin));
        }
        catch (Exception ex)
        {
            Log.Error("渲染失败", ex);
            Update(state, cached.Count > 0
                ? BuildCard(state, cached, "刷新失败：" + ex.Message)
                : CardBuilder.MessageCard("加载失败", ex.Message, error: true));
        }
    }

    private static JsonObject BuildCard(WidgetState state, IReadOnlyList<DeviceState> states, string? notice)
    {
        var hub = HaierHub.Instance;
        if (state.Size != CardBuilder.SizeSmall && !string.IsNullOrEmpty(state.ExpandedId))
        {
            // 展开某台设备：整卡切到单设备视图（列表 + 全部控件放不进一张卡）
            var focus = states.FirstOrDefault(s => s.Device.Id == state.ExpandedId);
            if (focus is not null && focus.Controls.Count > 0)
            {
                return CardBuilder.DeviceCard(focus, state.Size, back: true);
            }
        }
        return CardBuilder.HomeCard(states, state.Size, hub.Account, hub.RefreshedAt, notice);
    }

    /// <summary>设备状态变化（网关推送 / 控制结果 / 刷新）：合并 250ms 内的变化后重画所有小组件。</summary>
    private static void OnDeviceChanged(string deviceId)
    {
        lock (RenderGate)
        {
            _renderTimer ??= new System.Threading.Timer(_ => RenderAllFromCache(), null, Timeout.Infinite, Timeout.Infinite);
            _renderTimer.Change(250, Timeout.Infinite);
        }
    }

    private static void RenderAllFromCache()
    {
        try
        {
            var hub = HaierHub.Instance;
            if (!hub.LoggedIn)
            {
                return;
            }
            var states = hub.Snapshot();
            foreach (var w in Widgets.Values)
            {
                if (!w.ShowSettings)
                {
                    Update(w, BuildCard(w, states, null));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("重画小组件失败", ex);
        }
    }

    private static void OnSessionChanged()
    {
        foreach (var w in Widgets.Values)
        {
            w.ShowSettings = false;
            w.ExpandedId = "";
            _ = RenderAsync(w, HaierHub.ActivateMinInterval);
        }
    }

    private static void Update(WidgetState state, JsonObject card)
    {
        try
        {
            var template = card.ToJsonString(HaierApi.CompactJson);
            lock (state)
            {
                if (template == state.LastTemplate)
                {
                    return;
                }
                state.LastTemplate = template;
            }
            var options = new WidgetUpdateRequestOptions(state.Id)
            {
                Template = template,
                Data = "{}",
                CustomState = state.ExpandedId,
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }
        catch (Exception ex)
        {
            Log.Error($"UpdateWidget {state.Id} 失败", ex);
        }
    }
}
