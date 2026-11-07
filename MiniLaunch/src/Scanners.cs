using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MiniLaunch;

public static class Scanners
{
    // ============ 自定义条目 ============
    public static List<Entry> ScanCustom(AppConfig cfg)
    {
        var list = new List<Entry>();
        foreach (var c in cfg.Custom)
        {
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            string name = string.IsNullOrWhiteSpace(c.Name)
                ? Path.GetFileNameWithoutExtension(c.Path)
                : c.Name;
            list.Add(Make(name, c.Path.Trim(), c.Args?.Trim() ?? "", EntrySource.Custom,
                iconRef: c.Path, keywords: c.Keywords, workDir: c.WorkDir ?? "", runAs: c.RunAs));
        }
        return list;
    }

    // ============ 开始菜单快捷方式 ============
    public static List<Entry> ScanStartMenu()
    {
        var list = new List<Entry>();
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            var files = new List<string>();
            CollectFiles(root, files);
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".url") continue;
                string name = Path.GetFileNameWithoutExtension(f).Trim();
                if (name.Length == 0) continue;
                string nl = name.ToLowerInvariant();
                if (nl.StartsWith("卸载") || nl.StartsWith("uninstall") || nl.StartsWith("帮助") ||
                    nl.Contains("readme") || nl.Contains("帮助")) continue;
                if (ext == ".lnk")
                {
                    list.Add(Make(name, f, "", EntrySource.StartMenu, f));
                }
                else
                {
                    string? url = ReadUrlLine(f, "URL");
                    if (string.IsNullOrEmpty(url)) continue;
                    string icon = ReadUrlLine(f, "IconFile") ?? "";
                    list.Add(Make(name, url!, "", EntrySource.StartMenu, icon.Length > 0 ? icon : url!));
                }
            }
        }
        return list;
    }

    static void CollectFiles(string dir, List<string> sink)
    {
        try { foreach (var f in Directory.EnumerateFiles(dir)) sink.Add(f); } catch { }
        try { foreach (var d in Directory.EnumerateDirectories(dir)) CollectFiles(d, sink); } catch { }
    }

    static string? ReadUrlLine(string file, string prefix)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
                    return line[(prefix.Length + 1)..].Trim();
            }
        }
        catch { }
        return null;
    }

    // ============ 注册表卸载信息（补充无快捷方式的软件） ============
    public static List<Entry> ScanRegistry()
    {
        var list = new List<Entry>();
        var seen = new HashSet<string>();
        void ScanHive(RegistryKey root, string sub)
        {
            using RegistryKey? k = root.OpenSubKey(sub, writable: false);
            if (k == null) return;
            string[] names;
            try { names = k.GetSubKeyNames(); } catch { return; }
            foreach (var keyName in names)
            {
                RegistryKey? ck = null;
                try { ck = k.OpenSubKey(keyName); } catch { }
                if (ck == null) continue;
                using (ck)
                {
                    string? disp = ck.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(disp)) continue;
                    if (ToInt(ck.GetValue("SystemComponent")) == 1) continue;
                    if (!string.IsNullOrEmpty(ck.GetValue("ReleaseType") as string)) continue;
                    if (IsNoise(disp)) continue;
                    string dispLower = disp.ToLowerInvariant();
                    if (!seen.Add(dispLower)) continue;

                    string? displayIcon = ck.GetValue("DisplayIcon") as string;
                    if (string.IsNullOrWhiteSpace(displayIcon)) continue;
                    if (displayIcon!.StartsWith('@')) continue;

                    int iconIndex = 0;
                    string exe = ParseDisplayIcon(displayIcon, ref iconIndex);
                    if (exe.Length == 0) continue;
                    if (!File.Exists(exe)) continue;

                    list.Add(Make(disp, exe, "", EntrySource.Registry, exe, iconIndex: iconIndex, workDir: Path.GetDirectoryName(exe) ?? ""));
                }
            }
        }

        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            ScanHive(hklm64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            ScanHive(hklm32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            ScanHive(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        return list;
    }

    static int ToInt(object? v)
    {
        if (v is int i) return i;
        if (v is string s && int.TryParse(s, out int r)) return r;
        return 0;
    }

    static bool IsNoise(string name)
    {
        string s = name.ToLowerInvariant();
        return s.Contains("update for") || s.Contains("security update") || s.Contains("hotfix") ||
               s.Contains(" redistributable") || s.Contains("(kb") || s.EndsWith(" driver") ||
               s.Contains("语言包") || s.Contains("language pack") || s.Contains("microsoft windows ");
    }

    static string ParseDisplayIcon(string di, ref int iconIndex)
    {
        string s = di.Trim().Trim('"');
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out int idx))
        {
            iconIndex = idx;
            s = s[..comma].Trim();
        }
        try { s = Environment.ExpandEnvironmentVariables(s); } catch { }
        return s;
    }

    // ============ UWP / 商店应用（powershell Get-StartApps，带磁盘缓存） ============
    public static List<Entry> ScanUwp(bool force)
    {
        var (cachedItems, cachedTicks) = ReadUwpCache();
        if (!force && cachedTicks > 0 &&
            DateTime.UtcNow.Ticks - cachedTicks < TimeSpan.FromDays(7).Ticks)
            return CacheToEntries(cachedItems);

        var apps = RunGetStartApps();
        if (apps == null) return CacheToEntries(cachedItems); // powershell 失败：沿用旧缓存

        long now = DateTime.UtcNow.Ticks;
        var items = new List<UwpCacheItem>();
        foreach (var a in apps)
        {
            if (string.IsNullOrEmpty(a.AppId) || !a.AppId.Contains('!')) continue; // 只留包化 AUMID
            items.Add(new UwpCacheItem { Name = a.Name, AppId = a.AppId, CachedTicks = now });
        }
        // 写盘时追加一条空名哨兵：即使一个包化应用都没有，也记录“已扫描”的时间戳，
        // 否则每次启动都会重复拉 powershell（缓存里没条目 → 判定为无缓存）。
        var write = new List<UwpCacheItem>(items.Count + 1);
        write.AddRange(items);
        write.Add(new UwpCacheItem { Name = "", AppId = "", CachedTicks = now });
        try { ConfigStore.SaveAtomic(Paths.UwpCachePath, JsonSerializer.Serialize(write, JsonCtx.Default.ListUwpCacheItem)); } catch { }
        return CacheToEntries(items);
    }

    /// <summary>读取 UWP 磁盘缓存：返回（有效条目，扫描时间戳）。</summary>
    public static (List<UwpCacheItem> Items, long Ticks) ReadUwpCache()
    {
        try
        {
            if (File.Exists(Paths.UwpCachePath))
            {
                var l = JsonSerializer.Deserialize(File.ReadAllText(Paths.UwpCachePath), JsonCtx.Default.ListUwpCacheItem);
                if (l != null)
                {
                    long ticks = 0;
                    var items = new List<UwpCacheItem>(l.Count);
                    foreach (var it in l)
                    {
                        if (it.CachedTicks > ticks) ticks = it.CachedTicks;
                        if (string.IsNullOrEmpty(it.Name) || string.IsNullOrEmpty(it.AppId)) continue; // 哨兵
                        items.Add(it);
                    }
                    return (items, ticks);
                }
            }
        }
        catch { }
        return (new List<UwpCacheItem>(), 0);
    }

    static List<Entry> CacheToEntries(List<UwpCacheItem> items)
    {
        var list = new List<Entry>(items.Count);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.Name) || string.IsNullOrEmpty(it.AppId)) continue;
            list.Add(Make(it.Name, "explorer.exe", "shell:AppsFolder\\" + it.AppId, EntrySource.Uwp,
                "shell32.dll", iconIndex: 2));
        }
        return list;
    }

    static List<StartAppItem>? RunGetStartApps()
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 关键：默认会用「父进程控制台代码页」解码管道输出（本机 GBK），
                // 而 powershell 按上面命令输出的是 UTF-8 → 中文乱码，且 GBK 双字节
                // 解码会把 JSON 里的引号吞掉，直接导致 JsonException（UWP 列表全丢）。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            p = Process.Start(psi);
            if (p == null) return null;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            // 两条管道都并行读：顺序 ReadToEnd 会在另一条管道写满时死锁（坑 #18），
            // 而且同步读会先无限阻塞，让后面的 WaitForExit 超时永远轮不到。
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(); } catch { }
                try { p.WaitForExit(3000); } catch { }
                return null;
            }
            // 进程已退出 → stdout 必已关闭，任务会立即完成
            string json = outTask.GetAwaiter().GetResult();
            try { errTask.Wait(1000); } catch { }
            return ParseStartAppsJson(json);
        }
        catch
        {
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>解析 Get-StartApps 的 JSON 文本：容忍 BOM、控制字符与孤立代理项。</summary>
    public static List<StartAppItem>? ParseStartAppsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        json = json.Trim().TrimStart('\uFEFF');
        if (json.Length == 0) return null;
        try
        {
            return ParseStartAppsJsonCore(json);
        }
        catch (Exception)
        {
            // 个别应用名带控制字符/孤立代理项 → 清洗后重试一次
            // （含 JsonException 与 System.Text.Json 转码孤立代理项时的 ArgumentException）
            try { return ParseStartAppsJsonCore(SanitizeJson(json)); }
            catch { return null; }
        }
    }

    static List<StartAppItem>? ParseStartAppsJsonCore(string json)
    {
        if (json.StartsWith('['))
            return JsonSerializer.Deserialize(json, JsonCtx.Default.ListStartAppItem);
        var one = JsonSerializer.Deserialize(json, JsonCtx.Default.StartAppItem);
        return one == null ? null : new List<StartAppItem> { one };
    }

    /// <summary>剔除 JSON 非法字符（控制字符、孤立代理项）。</summary>
    static string SanitizeJson(string json)
    {
        var sb = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c < 0x20)
            {
                if (c == '\n' || c == '\r' || c == '\t') sb.Append(c);
                continue;
            }
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < json.Length && char.IsLowSurrogate(json[i + 1])) { sb.Append(c).Append(json[i + 1]); i++; }
                else sb.Append('\uFFFD');
                continue;
            }
            if (char.IsLowSurrogate(c)) { sb.Append('\uFFFD'); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ============ 条目构建 ============
    public static Entry Make(string name, string launchPath, string args, EntrySource src,
        string iconRef, string keywords = "", string workDir = "", int iconIndex = 0, bool runAs = false)
    {
        var e = new Entry
        {
            Name = name,
            LaunchPath = launchPath,
            Args = args,
            WorkDir = workDir,
            RunAs = runAs,
            IconRef = iconRef ?? "",
            IconIndex = iconIndex,
            Source = src,
        };
        e.NameLower = name.ToLowerInvariant();
        string alias = Pinyin.Lookup(name);
        string fileLower = SafeLowerFileName(launchPath);
        string dirLower = SafeLowerDir(launchPath);
        e.HayLower = (e.NameLower + " " + alias + " " + (keywords ?? "").ToLowerInvariant() + " " + fileLower + " " + dirLower).Trim();
        e.UniqueKey = src + ":" + e.NameLower;
        e.RevealPath = src == EntrySource.Uwp ? "" : launchPath;
        return e;
    }

    static string SafeLowerFileName(string p)
    {
        try { return Path.GetFileName(p).ToLowerInvariant(); }
        catch { return ""; }
    }

    static string SafeLowerDir(string p)
    {
        try { return Path.GetDirectoryName(p)?.ToLowerInvariant() ?? ""; }
        catch { return ""; }
    }
}
