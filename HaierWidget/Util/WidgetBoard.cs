using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HaierWidget.Util;

/// <summary>Win11 小组件面板：用 ms-widgetboard: 协议打开。面板窗口是 TOPMOST 的，要压住它自己也得置顶。</summary>
public static class WidgetBoard
{
    private const string Protocol = "ms-widgetboard:";
    private const string BoardWindowClass = "WindowsDashboard";

    /// <summary>打开小组件面板（面板未安装 / 被策略禁用时返回 false）。</summary>
    public static bool Open()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Protocol) { UseShellExecute = true });
            Log.Info("打开小组件面板");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("打开小组件面板失败", ex);
            return false;
        }
    }

    /// <summary>面板当前是否展开（面板有多个同类窗口，只认可见的那个）。</summary>
    public static bool IsOpen()
    {
        var found = false;
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(64);
            if (GetClassName(h, cls, cls.Capacity) > 0 && cls.ToString() == BoardWindowClass && IsWindowVisible(h))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
