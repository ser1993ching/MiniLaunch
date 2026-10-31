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
    public string Hotkey { get; set; } = HotkeyParser.DefaultHotkey;
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
    /// <summary>默认热键：Ctrl+Space（与输入法切换冲突时可在托盘菜单里改）。</summary>
    public const string DefaultHotkey = "Ctrl+Space";

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

    /// <summary>把虚拟键翻译成显示名（用系统键名，兼容不同键位）。</summary>
    public static string VkName(uint vk)
    {
        try
        {
            uint sc = Win32.MapVirtualKeyW(vk, Win32.MAPVK_VK_TO_VSC);
            if (sc != 0)
            {
                var sb = new System.Text.StringBuilder(64);
                int lp = (int)(sc << 16);
                if (Win32.GetKeyNameTextW(lp, sb, 64) > 0 && sb.Length > 0)
                    return sb.ToString();
            }
        }
        catch { }
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return "VK" + vk.ToString("X2");
    }

    /// <summary>把修饰键 + 虚拟键拼回 "Ctrl+Alt+F5" 形式（Parse 的逆运算）。</summary>
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>(5);
        if ((mods & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(VkName(vk));
        return string.Join("+", parts);
    }

    /// <summary>从当前键盘状态读取修饰键（热键录制用）。</summary>
    public static uint ModsFromKeyboard()
    {
        uint mods = 0;
        if ((Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0) mods |= Win32.MOD_CONTROL;
        if ((Win32.GetKeyState(Win32.VK_MENU) & 0x8000) != 0) mods |= Win32.MOD_ALT;
        if ((Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0) mods |= Win32.MOD_SHIFT;
        if ((Win32.GetKeyState(Win32.VK_LWIN) & 0x8000) != 0 || (Win32.GetKeyState(Win32.VK_RWIN) & 0x8000) != 0)
            mods |= Win32.MOD_WIN;
        return mods;
    }

    /// <summary>是否为单独的修饰键（录制时应跳过）。</summary>
    public static bool IsModifierKey(int vk) =>
        vk is Win32.VK_CONTROL or Win32.VK_MENU or Win32.VK_SHIFT or Win32.VK_LWIN or Win32.VK_RWIN
           or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5; // L/R Shift/Ctrl/Alt
}
