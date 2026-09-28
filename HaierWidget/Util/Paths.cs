using System.Runtime.InteropServices;
using System.Text;

namespace HaierWidget.Util;

/// <summary>数据目录：带包身份时用 ApplicationData.LocalFolder，否则用 %LOCALAPPDATA%\HaierWidget。</summary>
public static class Paths
{
    public static string DataDir { get; }

    static Paths()
    {
        var env = Environment.GetEnvironmentVariable("HAIER_WIDGET_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            DataDir = env;
        }
        else if (HasPackageIdentity())
        {
            DataDir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        else
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaierWidget");
        }
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
    }

    public static string SessionFile => Path.Combine(DataDir, "session.json");
    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string LogFile => Path.Combine(DataDir, "widget.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private const int AppModelErrorNoPackage = 15700;

    public static bool HasPackageIdentity()
    {
        int len = 0;
        int rc = GetCurrentPackageFullName(ref len, null);
        return rc != AppModelErrorNoPackage;
    }
}
