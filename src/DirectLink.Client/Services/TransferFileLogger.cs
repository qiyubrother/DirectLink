using System.IO;

namespace DirectLink.Client.Services;

/// <summary>
/// 将传输日志写入本地文件：%LocalAppData%\DirectLink\logs\yyyy-MM-dd.log
/// </summary>
public static class TransferFileLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DirectLink", "logs");
    private static readonly object Lock = new();

    public static void Write(string category, string message)
    {
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {message}{Environment.NewLine}";
            lock (Lock)
                File.AppendAllText(file, line);
        }
        catch { /* 忽略写日志失败 */ }
    }

    /// <summary>调试连接/断开流程，写入单独文件便于排查</summary>
    public static void WriteConnectDebug(string step, string detail = "")
    {
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, "connect-debug.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {step}{(string.IsNullOrEmpty(detail) ? "" : " | " + detail)}{Environment.NewLine}";
            lock (Lock)
                File.AppendAllText(file, line);
        }
        catch { }
    }
}
