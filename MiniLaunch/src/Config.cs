using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniLaunch;

public class CustomEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public bool RunAs { get; set; }
    public string Keywords { get; set; } = "";
}

public class AppConfig
{
    public string Hotkey { get; set; } = "Alt+Space";
    public bool AutoStart { get; set; }
    public int MaxResults { get; set; } = 8;
    public List<CustomEntry> Custom { get; set; } = new();
}

public class UsageRec
{
    public int Count { get; set; }
    public long LastTicks { get; set; }
}

public class UsageDb
{
    public Dictionary<string, UsageRec> Recs { get; set; } = new();
}

public class UwpCacheItem
{
    public string Name { get; set; } = "";
    public string AppId { get; set; } = "";
    public long CachedTicks { get; set; }
}

// powershell Get-StartApps 输出项
public class StartAppItem
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("AppID")] public string AppId { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(UsageDb))]
[JsonSerializable(typeof(List<UwpCacheItem>))]
[JsonSerializable(typeof(StartAppItem))]
[JsonSerializable(typeof(List<StartAppItem>))]
public partial class JsonCtx : JsonSerializerContext { }

public static class Paths
{
    public static string Dir { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniLaunch");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string UsagePath => Path.Combine(Dir, "usage.json");
    public static string UwpCachePath => Path.Combine(Dir, "uwp-cache.json");

    public static void EnsureDir()
    {
        if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
    }
}

public static class ConfigStore
{
    public static AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(Paths.ConfigPath))
            {
                var c = JsonSerializer.Deserialize(File.ReadAllText(Paths.ConfigPath), JsonCtx.Default.AppConfig);
                if (c != null) return c;
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void SaveConfig(AppConfig c) => SaveAtomic(Paths.ConfigPath, JsonSerializer.Serialize(c, JsonCtx.Default.AppConfig));

    public static UsageDb LoadUsage()
    {
        try
        {
            if (File.Exists(Paths.UsagePath))
            {
                var u = JsonSerializer.Deserialize(File.ReadAllText(Paths.UsagePath), JsonCtx.Default.UsageDb);
                if (u != null) return u;
            }
        }
        catch { }
        return new UsageDb();
    }

    public static void SaveUsage(UsageDb u) => SaveAtomic(Paths.UsagePath, JsonSerializer.Serialize(u, JsonCtx.Default.UsageDb));

    public static void SaveAtomic(string path, string content)
    {
        Paths.EnsureDir();
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

public static class HotkeyParser
{
    /// <summary>解析 "Alt+Space" / "Ctrl+Alt+P" / "Ctrl+Shift+F1" 形式的热键串。</summary>
    public static (uint mods, uint vk, bool ok) Parse(string s)
    {
        uint mods = 0, vk = 0;
        bool keyOk = false;
        if (string.IsNullOrWhiteSpace(s)) return (0, 0, false);
        foreach (var raw in s.Split('+'))
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length == 0) return (0, 0, false);
            switch (t)
            {
                case "alt": mods |= Win32.MOD_ALT; continue;
                case "ctrl":
                case "control": mods |= Win32.MOD_CONTROL; continue;
                case "shift": mods |= Win32.MOD_SHIFT; continue;
                case "win":
                case "windows": mods |= Win32.MOD_WIN; continue;
            }
            if (keyOk) return (0, 0, false); // 出现第二个普通键
            if (t.Length == 1)
            {
                char c = t[0];
                if (c is >= 'a' and <= 'z') { vk = (uint)(c - 32); keyOk = true; }
                else if (c is >= '0' and <= '9') { vk = c; keyOk = true; }
            }
            else if (t[0] == 'f' && t.Length <= 3 && int.TryParse(t[1..], out int n) && n >= 1 && n <= 12)
            {
                vk = 0x70u + (uint)(n - 1); keyOk = true;
            }
            else
            {
                vk = t switch
                {
                    "space" => 0x20,
                    "tab" => 0x09,
                    "enter" or "return" => 0x0D,
                    "esc" or "escape" => 0x1B,
                    _ => 0,
                };
                keyOk = vk != 0;
            }
            if (!keyOk) return (0, 0, false);
        }
        return mods != 0 && keyOk ? (mods, vk, true) : (0, 0, false);
    }
}
