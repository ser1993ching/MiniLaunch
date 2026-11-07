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
        string tmpDir = "";
        try
        {
            // 用临时目录隔离，避免污染真实配置
            tmpDir = Path.Combine(Path.GetTempPath(), "MiniLaunch-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Paths.Dir = tmpDir;
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
            // 清理临时目录（否则每次自检都会在 %TEMP% 留下一个垃圾目录）
            try { if (tmpDir.Length > 0 && Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
            catch { }
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

        // ---- 1c. 开机自启：命令行解析 / 路径判定 / 注册表往返 ----
        Check(AutoStart.ParseExeFromCommand("\"C:\\a b\\MiniLaunch.exe\"") == "C:\\a b\\MiniLaunch.exe",
            "自启命令行解析：带引号路径", AutoStart.ParseExeFromCommand("\"C:\\a b\\MiniLaunch.exe\"") ?? "null");
        Check(AutoStart.ParseExeFromCommand("C:\\x\\MiniLaunch.exe --flag") == "C:\\x\\MiniLaunch.exe",
            "自启命令行解析：带参数时取 exe", AutoStart.ParseExeFromCommand("C:\\x\\MiniLaunch.exe --flag") ?? "null");
        Check(AutoStart.ParseExeFromCommand("") == null && AutoStart.ParseExeFromCommand(null) == null,
            "自启命令行解析：空值返回 null");
        Check(AutoStart.SameExe("\"C:\\X\\MiniLaunch.EXE\"", "c:\\x\\minilaunch.exe"),
            "自启路径比较：忽略大小写与引号");
        Check(AutoStart.IsBuildOutput("E:\\p\\MiniLaunch\\bin\\Debug\\net8.0-windows\\MiniLaunch.exe"),
            "自启目标判定：构建目录视为不稳定");        Check(!AutoStart.IsBuildOutput(AutoStart.InstalledExe),
            "自启目标判定：本地安装目录视为稳定", AutoStart.InstalledExe);

        // 注册表往返：用临时值名，不碰用户真实的 MiniLaunch 项
        const string tmpVal = "MiniLaunch__selftest";
        Check(!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath),
            "Environment.ProcessPath 指向真实可执行文件", Environment.ProcessPath ?? "null");
        string asExe = Environment.ProcessPath ?? "";
        bool w = File.Exists(asExe) && AutoStart.TryEnable(asExe, tmpVal);
        bool rb = w && AutoStart.SameExe(AutoStart.ReadRawCommand(tmpVal) ?? "", asExe);
        AutoStart.TryDisable(tmpVal);
        bool cleaned = AutoStart.ReadRawCommand(tmpVal) == null;
        Check(w, "自启注册表：写入 Run 项成功");
        Check(rb, "自启注册表：写入后回读一致");
        Check(cleaned, "自启注册表：删除后回读为空");

        // 安装到本地：传入临时目录，避免在自检时污染 %LOCALAPPDATA%
        string tmpDir = Path.Combine(Path.GetTempPath(), "MiniLaunch-inst-" + Guid.NewGuid().ToString("N")[..6]);
        string tmpExe = Path.Combine(tmpDir, "fake.exe");
        try
        {
            Directory.CreateDirectory(tmpDir);
            File.WriteAllText(tmpExe, "fake-exe-content");
            var (iok, imsg) = AutoStart.InstallLocal(tmpExe, tmpDir);
            string installed = Path.Combine(tmpDir, "MiniLaunch.exe");
            Check(iok && File.Exists(installed), "安装到本地：复制 exe 到目标目录", imsg);
            bool same = false;
            try { same = File.ReadAllText(installed) == "fake-exe-content"; } catch { }
            Check(same, "安装到本地：内容一致");
        }
        catch { }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
        var (badOk, _) = AutoStart.InstallLocal("C:/nonexistent/x.exe", tmpDir);
        Check(!badOk, "安装到本地：源文件不存在时拒绝安装");

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
        var (uwpCacheNow, _) = Scanners.ReadUwpCache();
        Check(uwpCount == uwpCacheNow.Count, "UWP 扫描执行且与磁盘缓存一致", $"count={uwpCount} cache={uwpCacheNow.Count}");

        // ---- 6b. UWP JSON 解析容错（BOM / 控制字符 / 孤立代理项）----
        string j1 = "\uFEFF[{\"Name\":\"A\u0001B\",\"AppID\":\"X_1!App\"}]";
        var parsed1 = Scanners.ParseStartAppsJson(j1);
        Check(parsed1 != null && parsed1.Count == 1 && parsed1[0].AppId == "X_1!App",
            "UWP JSON 解析容错（BOM + 控制字符）", parsed1 == null ? "null" : $"count={parsed1.Count}");
        string j2 = "[{\"Name\":\"C\uD800D\",\"AppID\":\"Y_1!App\"}]";
        var parsed2 = Scanners.ParseStartAppsJson(j2);
        Check(parsed2 != null && parsed2.Count == 1 && parsed2[0].AppId == "Y_1!App",
            "UWP JSON 解析容错（孤立代理项）", parsed2 == null ? "null" : $"count={parsed2.Count}");

        // ---- 6c. UWP 缓存能落盘并被复用（修复前因管道编码错误永远写不出缓存）----
        var (cacheItems, cacheTicks) = Scanners.ReadUwpCache();
        Check(cacheTicks > 0, "UWP 缓存已落盘（含扫描时间戳）", $"ticks={cacheTicks} items={cacheItems.Count}");
        if (cacheTicks > 0)
        {
            var cachedEntries = Scanners.ScanUwp(false); // 应直接走磁盘缓存，不再拉 powershell
            Check(cachedEntries.Count == cacheItems.Count, "UWP 缓存复用（不再重复拉 powershell）",
                $"entries={cachedEntries.Count} cache={cacheItems.Count}");
        }
        else
        {
            Check(true, "UWP 缓存复用（本机无法运行 powershell，跳过校验）");
        }

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

        // 回归：空查询必须「频率优先」而非「最近使用优先」
        // （历史 bug：Count 权重拼进 Ticks 的 long 被淹没，30 天前用过 100 次的会输给今天用过 1 次的）
        var uIdle = new UsageDb();
        var engIdle = new SearchEngine(uIdle);
        var eOld = Scanners.Make("老牌常用", @"C:\x\old.exe", "", EntrySource.StartMenu, @"C:\x\old.exe");
        var eNew = Scanners.Make("新近偶尔", @"C:\y\new.exe", "", EntrySource.StartMenu, @"C:\y\new.exe");
        uIdle.Recs[eOld.UniqueKey] = new UsageRec { Count = 100, LastTicks = DateTime.UtcNow.Ticks - TimeSpan.FromDays(30).Ticks };
        uIdle.Recs[eNew.UniqueKey] = new UsageRec { Count = 1, LastTicks = DateTime.UtcNow.Ticks };
        engIdle.SetEntries(new List<Entry> { eNew, eOld });
        var rIdle = engIdle.Query("", 10);
        Check(rIdle.Count == 2 && rIdle[0] == eOld, "空查询频率优先于最近使用", $"r=[{string.Join(",", rIdle.Select(e => e.Name))}]");

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

        // ---- 9b. 图标后台提取（UI 线程不再同步取图标，避免首屏卡顿）----
        using (var icons = new IconCache())
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var ne = Scanners.Make("记事本", "notepad.exe", "", EntrySource.Custom, notepad);
            Check(!icons.TryGet(ne, out _), "图标缓存初始为空");
            icons.Request(ne);
            bool got = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                icons.Pump();
                if (icons.TryGet(ne, out var hi) && hi != IntPtr.Zero) { got = true; break; }
                Thread.Sleep(10);
            }
            Check(got, "图标后台提取后可被 UI 取用");
            icons.Request(ne); // 已在缓存：不应重复排队
            Check(icons.TryGet(ne, out var h3) && h3 != IntPtr.Zero, "图标缓存命中后不再重复提取");
        }

        // ---- 10. 扫描结果完整性 ----
        var full = EntryMerger.Merge(Scanners.ScanCustom(cfg2), sm, reg, new());
        Check(full.Count >= 10, "合并后总条目数量合理", $"count={full.Count}");
        Check(full.All(e => e.HayLower.Length > 0 && e.UniqueKey.Length > 0), "条目检索字段已构建");

        // ---- 11. 文本封送：GetWindowTextW 必须走 Unicode ----
        // 回归防护：漏 CharSet.Unicode 时 "docker" 会被读成 "d"（只能按首字母搜索）
        IntPtr he = Win32.CreateWindowExW(0, "EDIT", "", Win32.WS_POPUP, 0, 0, 200, 40,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (he != IntPtr.Zero)
        {
            const string sample = "docker 微信";
            Win32.SetWindowTextW(he, sample);
            var tsb = new StringBuilder(256);
            Win32.GetWindowTextW(he, tsb, 256);
            Check(tsb.ToString() == sample, "GetWindowTextW 多字符不被截断", $"读回“{tsb}”");
            Win32.DestroyWindow(he);
        }
        else Check(false, "创建 EDIT 用于文本封送回归", "CreateWindowExW 失败");

        // ---- 12. 应用图标（内嵌于 exe，资源管理器 / 任务栏 / 托盘共用）----
        string? selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe))
        {
            uint iconCount = Win32.ExtractIconExW(selfExe, -1, null, null, 0);
            Check(iconCount > 0, "exe 内嵌应用图标可提取", $"图标数={iconCount}");

            // 托盘图标即取自自身 exe（见 Tray.MakeNid），此处确认该链路可用
            var fi = new Win32.SHFILEINFOW();
            IntPtr r = Win32.SHGetFileInfoW(selfExe, 0, ref fi,
                (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(),
                Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            Check(r != IntPtr.Zero && fi.hIcon != IntPtr.Zero, "托盘图标来源可用（SHGetFileInfo 取自身 exe 图标）");
            if (fi.hIcon != IntPtr.Zero) Win32.DestroyIcon(fi.hIcon);
        }
        else Check(false, "获取自身可执行文件路径");

        // ---- 13. 应用图标加载（exe 模块内的 Win32 图标资源）----
        // 单文件发布且开启 PublishReadyToRun 时 SDK 会丢弃图标资源，此断言能第一时间发现
        IntPtr appIcon = AppIcon.Load();
        Check(appIcon != IntPtr.Zero, "应用图标：exe 模块资源可加载");
        if (appIcon != IntPtr.Zero) Win32.DestroyIcon(appIcon);

        // ---- 14. DPI 兼容兜底（Win10 1703 以前无 SetProcessDpiAwarenessContext / 1607 以前无 GetDpiForWindow）----
        try
        {
            Dpi.EnableAwareness();
            Check(true, "DPI 感知设置不抛异常");
        }
        catch (Exception ex) { Check(false, "DPI 感知设置不抛异常", ex.GetType().Name); }

        IntPtr hDpi = Win32.CreateWindowExW(0, "STATIC", "", Win32.WS_POPUP, 0, 0, 10, 10,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (hDpi != IntPtr.Zero)
        {
            uint dpi1 = Dpi.ForWindow(hDpi);
            uint dpi2 = Dpi.FromDeviceCaps(hDpi);
            Check(dpi1 >= 48 && dpi1 <= 480, "窗口 DPI 读取合理", $"GetDpiForWindow={dpi1}");
            Check(dpi2 >= 48 && dpi2 <= 480, "DPI 兜底路径（GetDeviceCaps）可用", $"={dpi2}");
            Win32.DestroyWindow(hDpi);
        }
        else Check(false, "创建窗口用于 DPI 自检");

        // ---- 15. 主窗口布局：客户区必须等于 DPI 重算后的尺寸 ----
        // 回归：窗口按 96 DPI 创建后只在 WM_CREATE 重算度量却不 resize，
        // 高 DPI 主屏下底部行/底栏被裁剪（本机 96 DPI 时此断言平凡通过，高 DPI 机器上才有鉴别力）
        var mw = MainWindow.Create(new AppConfig(), new SearchEngine(new UsageDb()), new IconCache(),
            (e, ra) => { }, () => { }, files => { });
        Win32.GetClientRect(mw.Hwnd, out var mrc);
        Check(mrc.Width == mw.LayoutWidth && mrc.Height == mw.LayoutHeight,
            "主窗口客户区与 DPI 布局一致", $"client={mrc.Width}x{mrc.Height} layout={mw.LayoutWidth}x{mw.LayoutHeight}");
        Win32.DestroyWindow(mw.Hwnd);
    }
}
