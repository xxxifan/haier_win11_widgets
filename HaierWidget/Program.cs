using System.Text;
using System.Text.Json.Nodes;
using HaierWidget.Com;
using HaierWidget.Haier;
using HaierWidget.Login;
using HaierWidget.Util;
using HaierWidget.Widget;
using Microsoft.Windows.Widgets.Providers;

namespace HaierWidget;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>WinExe 没有控制台；命令行模式下挂到父进程的控制台上输出。</summary>
    private static void UseParentConsole()
    {
        if (AttachConsole(-1))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.WriteLine();
        }
    }

    /// <summary>没有任何小组件时，进程最多再驻留多久。</summary>
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(2);

    [MTAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--") && args[0] != "--login")
        {
            UseParentConsole();
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("未观察的任务异常", e.Exception); e.SetObserved(); };

        var mode = args.Length > 0 ? args[0] : "";
        try
        {
            switch (mode)
            {
                case "-RegisterProcessAsComServer":
                    return RunComServer();
                case "--dump-card":
                    return DumpCard(args.Skip(1).ToArray()).GetAwaiter().GetResult();
                case "--selftest":
                    return SelfTest.Run();
                case "--set":
                    return SetValue(args.Skip(1).ToArray()).GetAwaiter().GetResult();
                case "--logout":
                    HaierHub.Instance.Logout();
                    Console.WriteLine("已退出登录");
                    return 0;
                default:
                    // 无参数 / --login / 协议激活 haierwidget://login：显示登录窗口
                    return RunLoginForm();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"启动失败 (mode={mode})", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunComServer()
    {
        Log.Info($"COM 服务启动 pid={Environment.ProcessId} data={Paths.DataDir}");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        uint cookie = ClassObject.Register(typeof(WidgetProvider).GUID, new WidgetProviderFactory<WidgetProvider>());
        try
        {
            // 预先恢复已有小组件并连上数据源
            _ = new WidgetProvider();
            // 没有小组件后等一小段时间再退出（面板重新添加时会重新拉起进程）
            while (true)
            {
                WidgetProvider.NoWidgets.WaitOne();
                Thread.Sleep(IdleExit);
                if (WidgetProvider.NoWidgets.WaitOne(0))
                {
                    Log.Info("没有小组件，退出");
                    break;
                }
            }
        }
        finally
        {
            ClassObject.Revoke(cookie);
        }
        return 0;
    }

    [STAThread]
    private static int RunLoginForm()
    {
        var t = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LoginForm());
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }

    /// <summary>联机调试：经网关下发一条命令。用法：--set <设备ID或名称> <属性> <值></summary>
    private static async Task<int> SetValue(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法: --set <设备ID或名称> <属性> <值>");
            return 2;
        }
        var hub = HaierHub.Instance;
        var devices = await hub.GetDevicesAsync();
        var dev = devices.FirstOrDefault(d => d.Id == args[0] || d.Name.Contains(args[0]))
            ?? throw new HaierApiException($"找不到设备 {args[0]}");
        var before = await hub.LoadDeviceAsync(dev.Id, refresh: true);
        before.Values.TryGetValue(args[1], out var old);
        Console.WriteLine($"{dev.Name} {args[1]}: {old} -> {args[2]}");
        var error = await hub.ControlAsync(dev.Id, args[1], args[2]);
        Console.WriteLine(error is null ? "成功" : $"失败: {error}");
        await Task.Delay(2000);
        var after = hub.TryGetState(dev.Id);
        after?.Values.TryGetValue(args[1], out var now);
        Console.WriteLine($"当前值: {(after is not null && after.Values.TryGetValue(args[1], out var cur) ? cur : "?")}");
        await hub.ReleaseAsync();
        return error is null ? 0 : 1;
    }

    /// <summary>离线调试：打印指定设备 / 尺寸的卡片 JSON。用法：--dump-card [small|medium|large] [deviceId]</summary>
    private static async Task<int> DumpCard(string[] args)
    {
        var size = args.Length > 0 ? args[0] : CardBuilder.SizeMedium;
        var hub = HaierHub.Instance;
        if (!hub.LoggedIn)
        {
            Console.WriteLine(CardBuilder.LoginCard(size).ToJsonString(new(HaierApi.CompactJson) { WriteIndented = true }));
            return 0;
        }
        var states = await hub.LoadAllAsync(TimeSpan.Zero);
        var options = new System.Text.Json.JsonSerializerOptions(HaierApi.CompactJson) { WriteIndented = true };
        if (args.Length < 2)
        {
            Console.WriteLine(CardBuilder.HomeCard(states, size, hub.Account, hub.RefreshedAt).ToJsonString(options));
        }
        else
        {
            var state = states.FirstOrDefault(s => s.Device.Id == args[1] || s.Device.Name.Contains(args[1]))
                ?? throw new HaierApiException($"找不到设备 {args[1]}");
            Console.WriteLine(CardBuilder.DeviceCard(state, size, back: true).ToJsonString(options));
        }
        await hub.ReleaseAsync();
        return 0;
    }
}
