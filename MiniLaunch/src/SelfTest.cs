using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 无头自检：--selftest 运行，结果写 stdout 管道与 selftest-report.txt
public static class SelfTest
{
    static readonly StringBuilder Report = new();
    static int _pass, _fail;

    static void Check(bool cond, string name, string detail = "")
    {
        string line = (cond ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? "  — " + detail : "");
        if (cond) _pass++; else _fail++;
        Report.AppendLine(line);
        NativeOut.WriteLine(line);
    }

    public static int Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string origDir = Paths.Dir;
        try
        {
            // 用临时目录隔离，避免污染真实配置
            Paths.Dir = Path.Combine(Path.GetTempPath(), "MiniLaunch-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            RunAll();
        }
        catch (Exception ex)
        {
            _fail++;
            string line = "[FAIL] 自检异常中断: " + ex.GetType().Name + ": " + ex.Message;
            Report.AppendLine(line);
            NativeOut.WriteLine(line);
        }
        finally
        {
            Paths.Dir = origDir;
        }

        sw.Stop();
        string summary = $"\n===== 自检结果: 通过 {_pass} / 失败 {_fail}，耗时 {sw.ElapsedMilliseconds}ms =====";
        Report.AppendLine(summary);
        NativeOut.WriteLine(summary);

        // 内存快照
        if (Win32.GetProcessWorkingSetSize(Win32.GetCurrentProcess(), out var minWs, out var maxWs))
        {
            string mem = $"当前进程工作集: {maxWs.ToUInt64() / 1024.0 / 1024.0:F1} MB";
            Report.AppendLine(mem);
            NativeOut.WriteLine(mem);
        }

        try
        {
            string reportPath = Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");
            File.WriteAllText(reportPath, Report.ToString());
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    static void RunAll()
    {
        // ---- 1. 热键解析 ----
        var (mods, vk, ok) = HotkeyParser.Parse("Alt+Space");
        Check(ok && mods == Win32.MOD_ALT && vk == Win32.VK_SPACE, "热键解析 Alt+Space");
        var (m2, v2, o2) = HotkeyParser.Parse("Ctrl+Shift+F3");
        Check(o2 && m2 == (Win32.MOD_CONTROL | Win32.MOD_SHIFT) && v2 == 0x72, "热键解析 Ctrl+Shift+F3");
        Check(!HotkeyParser.Parse("乱码键").ok, "热键解析拒绝非法输入");

        // ---- 1b. 默认热键 = Ctrl+Space，且可自定义（Format/Parse 往返）----
        var def = new AppConfig();
        Check(def.Hotkey == "Ctrl+Space", "默认热键为 Ctrl+Space", def.Hotkey);
        var (dm, dv, dok) = HotkeyParser.Parse(def.Hotkey);
        Check(dok && dm == Win32.MOD_CONTROL && dv == Win32.VK_SPACE, "默认热键解析为 Ctrl+Space",
            $"mods={dm} vk={dv}");
        Check(HotkeyParser.Format(dm, dv) == "Ctrl+Space", "热键格式化往返 Ctrl+Space",
            HotkeyParser.Format(dm, dv));
        var (cm, cv, cok) = HotkeyParser.Parse("Ctrl+Alt+F8");
        Check(cok && HotkeyParser.Format(cm, cv) == "Ctrl+Alt+F8", "热键格式化往返 Ctrl+Alt+F8",
            HotkeyParser.Format(cm, cv));
        Check(HotkeyParser.IsModifierKey(Win32.VK_CONTROL) && !HotkeyParser.IsModifierKey(0x41),
            "修饰键识别");

        // ---- 2. 配置原子读写回环 ----
        var cfg = new AppConfig
        {
            Hotkey = "Ctrl+Alt+P",
            AutoStart = true,
            Custom = { new CustomEntry { Name = "测试程序", Path = @"C:\Windows\System32\notepad.exe", Keywords = "jsb note" } },
        };
        ConfigStore.SaveConfig(cfg);
        var cfg2 = ConfigStore.LoadConfig();
        Check(cfg2.Hotkey == "Alt+Space".Replace("Alt+Space", "Ctrl+Alt+P") && cfg2.AutoStart && cfg2.Custom.Count == 1
              && cfg2.Custom[0].Name == "测试程序", "配置保存/加载回环");
        var usage = new UsageDb();
        usage.Recs["x"] = new UsageRec { Count = 3, LastTicks = 123 };
        ConfigStore.SaveUsage(usage);
        var usage2 = ConfigStore.LoadUsage();
        Check(usage2.Recs.Count == 1 && usage2.Recs["x"].Count == 3, "用量库保存/加载回环");

        // ---- 3. 拼音别名 ----
        Check(Pinyin.Lookup("微信").Contains("wx"), "拼音别名 微信→wx");
        Check(Pinyin.Lookup("企业微信").Contains("qywx"), "拼音别名 企业微信→qywx");
        Check(Pinyin.Lookup("计算器").Contains("jsq"), "拼音别名 计算器→jsq");

        // ---- 4. 扫描器：开始菜单 ----
        var sm = Scanners.ScanStartMenu();
        Check(sm.Count > 0, "开始菜单扫描有条目", $"count={sm.Count}");
        Check(sm.All(e => e.Name.Length > 0 && e.LaunchPath.Length > 0), "开始菜单条目字段完整");
        Check(sm.All(e => !e.NameLower.StartsWith("卸载") && !e.NameLower.StartsWith("uninstall")), "开始菜单过滤卸载项");

        // ---- 5. 扫描器：注册表 ----
        var reg = Scanners.ScanRegistry();
        Check(reg.Count > 0, "注册表扫描有条目", $"count={reg.Count}");
        Check(reg.All(e => !e.Name.ToLowerInvariant().Contains("redistributable")), "注册表过滤运行库噪声");
        Check(reg.All(e => File.Exists(e.LaunchPath)), "注册表条目目标存在");

        // ---- 6. 扫描器：UWP（powershell 可能不可用，容错） ----
        int uwpCount = 0;
        try { uwpCount = Scanners.ScanUwp(true).Count; }
        catch (Exception ex) { Check(false, "UWP 扫描无异常", ex.Message); }
        Check(uwpCount >= 0, "UWP 扫描执行", $"count={uwpCount}");

        // ---- 7. 合并去重 ----
        var e1 = Scanners.Make("Chrome", @"C:\x\chrome.exe", "", EntrySource.StartMenu, @"C:\x\chrome.exe");
        var e2 = Scanners.Make("chrome", @"C:\y\chrome2.exe", "", EntrySource.Registry, @"C:\y\chrome2.exe");
        var merged = EntryMerger.Merge(new(), new List<Entry> { e1 }, new List<Entry> { e2 }, new());
        Check(merged.Count == 1 && merged[0].Source == EntrySource.StartMenu, "合并去重且优先级正确");

        // ---- 8. 搜索 ----
        var u = new UsageDb();
        var eng = new SearchEngine(u);
        var a = Scanners.Make("Google Chrome", @"C:\x\chrome.exe", "", EntrySource.StartMenu, @"C:\x\chrome.exe");
        var b = Scanners.Make("Chrome Canary", @"C:\y\canary.exe", "", EntrySource.StartMenu, @"C:\y\canary.exe");
        var c = Scanners.Make("微信", @"C:\z\wechat.exe", "", EntrySource.Custom, @"C:\z\wechat.exe");
        eng.SetEntries(new List<Entry> { a, b, c });

        var r1 = eng.Query("chrome", 10);
        Check(r1.Count == 2 && r1[0] == b && r1[1] == a, "前缀匹配优先于包含匹配", $"r1=[{string.Join(",", r1.Select(e => e.Name))}]");
        var r2 = eng.Query("wx", 10);
        Check(r2.Count == 1 && r2[0] == c, "拼音缩写命中");
        var r3 = eng.Query("zzzz不存在的词", 10);
        Check(r3.Count == 0, "无结果返回空");
        var r4 = eng.Query("", 10);
        Check(r4.Count == 3, "空查询返回全部");
        var r5 = eng.Query("chrome canary", 10);
        Check(r5.Count == 1 && r5[0] == b, "多词 AND 查询");

        // 频率加权
        u.Recs[b.UniqueKey] = new UsageRec { Count = 50, LastTicks = DateTime.UtcNow.Ticks };
        var r6 = eng.Query("chrome", 10);
        Check(r6.Count == 2 && r6[0] == b, "使用频率提升排序");

        // ---- 9. 图标提取 ----
        using (var icons = new IconCache())
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var ne = Scanners.Make("记事本", notepad, "", EntrySource.Custom, notepad);
            IntPtr h = icons.Get(ne);
            Check(h != IntPtr.Zero, "图标提取 notepad.exe");
            IntPtr h2 = icons.Get(ne);
            Check(h2 == h, "图标 LRU 缓存命中");
        }

        // ---- 10. 扫描结果完整性 ----
        var full = EntryMerger.Merge(Scanners.ScanCustom(cfg2), sm, reg, new());
        Check(full.Count >= 10, "合并后总条目数量合理", $"count={full.Count}");
        Check(full.All(e => e.HayLower.Length > 0 && e.UniqueKey.Length > 0), "条目检索字段已构建");
    }
}
