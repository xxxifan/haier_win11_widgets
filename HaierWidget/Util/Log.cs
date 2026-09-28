using System.Text;

namespace HaierWidget.Util;

/// <summary>简单的文件日志（COM 激活的进程没有控制台，只能靠它排错）。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var path = Paths.LogFile;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
        try { Console.Error.Write(line); } catch { }
    }
}
