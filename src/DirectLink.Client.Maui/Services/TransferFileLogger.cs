using System.IO;

namespace DirectLink.Client.Maui.Services;

public static class TransferFileLogger
{
    private static string LogDir =>
        Path.Combine(FileSystem.AppDataDirectory, "DirectLink", "logs");
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
        catch { }
    }
}
