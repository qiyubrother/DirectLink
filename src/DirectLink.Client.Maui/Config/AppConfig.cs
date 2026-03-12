using System.IO;
using System.Text.Json;

namespace DirectLink.Client.Maui.Config;

public class AppConfig
{
    public string ClientId { get; set; } = "";
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 50000;
    public string SaveDirectory { get; set; } = "";

    private static string ConfigPath =>
        Path.Combine(FileSystem.AppDataDirectory, "DirectLink", "config.json");

    public static AppConfig Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var c = JsonSerializer.Deserialize<AppConfig>(json);
                if (c != null)
                {
                    if (string.IsNullOrEmpty(c.ClientId) || c.ClientId.Length != 3 || !c.ClientId.All(char.IsDigit))
                        c.ClientId = GenerateClientId();
                    if (string.IsNullOrEmpty(c.SaveDirectory))
                        c.SaveDirectory = Path.Combine(FileSystem.AppDataDirectory, "DirectLinkReceived");
                    return c;
                }
            }
        }
        catch { }
        var config = new AppConfig
        {
            ClientId = GenerateClientId(),
            SaveDirectory = Path.Combine(FileSystem.AppDataDirectory, "DirectLinkReceived")
        };
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static string GenerateClientId() => new Random().Next(100, 1000).ToString();
}
