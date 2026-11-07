using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MiniLaunch;

public static class Program
{
    const string MutexName = "Local\\MiniLaunch_SingleInstance";
    const string ShowEventName = "Local\\MiniLaunch_ShowSignal";

    static AppConfig _cfg = new();
    static UsageDb _usage = new();
    static SearchEngine _search = null!;
    static IconCache _icons = null!;
    static MainWindow? _win;
    static int _scanning;

    /// <summary>热键是否注册成功（诊断用）。</summary>
    public static bool HotkeyOk { get; private set; }

    /// <summary>UWP 扫描是否已完成（诊断用）。</summary>
    public static bool UwpScanned { get; private set; }

    /// <summary>启动瞬间的路径探针（诊断用）。</summary>
    static string _startupDiag = "";

    /// <summary>窗口首次显示信号：UWP 扫描（需拉 powershell）拖到首次显示之后再做。</summary>
    static readonly ManualResetEventSlim FirstShown = new(false);
    static int _firstShown;

    public static AppConfig Cfg => _cfg;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args.Contains("--selftest"))
            return SelfTest.Run();

        Paths.EnsureDir();

        // 单实例检查必须先于配置读写/自愈：否则第二实例也会跑 HealAutoStart
        // 并写 config/报告，与主实例竞争写盘。
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                ev.Set();
            }
            catch { }
            return 0;
        }

        bool firstRun = !File.Exists(Paths.ConfigPath);
        _startupDiag = $"firstRun={firstRun} | Dir={Paths.Dir} | ConfigPath={Paths.ConfigPath} | Exists={File.Exists(Paths.ConfigPath)} | APPDATA={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}";
        _cfg = ConfigStore.LoadConfig();
        _usage = ConfigStore.LoadUsage();

        // 开机自启自愈：注册表是「真相源」，config.json 只是用户意图的备份。
        // 历史问题「设了开机自启但重启后没启动」的根因就是：注册表项指向的文件
        // 失效了（构建目录被清理/搬走），却没有任何人修正它。
        // firstRun = 「配置文件不存在」，而 Heal 的第三个参数是「配置文件存在」——语义相反，必须取反
        HealAutoStart(configExists: !firstRun);
        _search = new SearchEngine(_usage);
        _icons = new IconCache();

        Dpi.EnableAwareness();

        StartScan(false);

        var signalThread = new Thread(WaitShowSignal) { IsBackground = true };
        signalThread.Start();

        _win = MainWindow.Create(_cfg, _search, _icons, OnLaunch, () => StartScan(true), AddFiles);

        // 图标后台提取完成 → 通知 UI 线程重绘（不在 WM_PAINT 里同步取图标）
        _icons.OnIconReady = () =>
        {
            var w = _win;
            if (w != null) Win32.PostMessageW(w.Hwnd, Win32.WM_APP_ICON_READY, IntPtr.Zero, IntPtr.Zero);
        };

        Tray.Init(_win.Hwnd);

        var (mods, vk, ok) = HotkeyParser.Parse(_cfg.Hotkey);
        bool hkOk = ok && Win32.RegisterHotKey(_win.Hwnd, 1, mods | Win32.MOD_NOREPEAT, vk);
        HotkeyOk = hkOk;
        if (!hkOk)
            Tray.Notify("热键注册失败", $"“{_cfg.Hotkey}”可能被其他程序占用，右键托盘图标可重新设置。");

        if (firstRun)
        {
            ConfigStore.SaveConfig(_cfg); // 首次运行即落盘默认配置，便于用户发现与编辑
            _win.ShowWin();
        }

        // 界面预览（截图/调试用）：不自动隐藏，也不抢前台以后就住
        if (args.Contains("--preview"))
        {
            _win.PreviewMode = true;
            _win.ShowWin();
        }
        // 预填搜索词（调试用）：--query docker --preview
        int qi = Array.IndexOf(args, "--query");
        if (qi >= 0 && qi + 1 < args.Length)
            _win.SetQuery(args[qi + 1]);
        if (args.Contains("--hotkey-dialog"))
        {
            _win.PreviewMode = true;
            HotkeyDialog.PreviewMode = true;
            ShowHotkeySettings();
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => ConfigStore.SaveUsage(_usage);

        int rc = RunLoop();

        Win32.UnregisterHotKey(_win.Hwnd, 1);
        Tray.Destroy();
        _icons.Dispose();
        ConfigStore.SaveUsage(_usage);
        return rc;
    }

    static int RunLoop()
    {
        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
        return 0;
    }

    static void WaitShowSignal()
    {
        try
        {
            // 句柄只建一次：每轮循环重建会在 dispose/重建的间隙丢掉第二实例的信号
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            while (true)
            {
                if (ev.WaitOne() && _win != null)
                    Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    /// <summary>用量落盘（WM_QUERYENDSESSION 调用；强制关机时 ProcessExit 不保证执行）。</summary>
    public static void FlushUsage()
    {
        try { ConfigStore.SaveUsage(_usage); } catch { }
    }

    public static void OnLaunch(Entry e, bool runAs)
    {
        bool ok = Launcher.Run(e, runAs);
        if (ok)
        {
            if (!_usage.Recs.TryGetValue(e.UniqueKey, out var r))
                _usage.Recs[e.UniqueKey] = r = new UsageRec();
            r.Count++;
            r.LastTicks = DateTime.UtcNow.Ticks;
            ConfigStore.SaveUsage(_usage);
        }
        _win?.HideWin();
    }

    public static void StartScan(bool foreground)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;
        var t = new Thread(() =>
        {
            try { DoScan(); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        })
        {
            IsBackground = true,
            Priority = foreground ? ThreadPriority.Normal : ThreadPriority.BelowNormal,
        };
        t.Start();
    }

    static void DoScan()
    {
        // 第一阶段（快，约 30ms）：自定义 + 开始菜单 + 注册表 → 先发布，窗口不必等 UWP
        var custom = Scanners.ScanCustom(_cfg);
        var sm = Scanners.ScanStartMenu();
        var reg = Scanners.ScanRegistry();
        PublishEntries(custom, sm, reg, new List<Entry>());

        // 第二阶段（慢，首次要 1.3s 拉 powershell）：等「窗口首次显示」或 3 秒超时（先到者触发）。
        // 开机自启常驻时窗口可能永远不显示——此前用 FirstShown.Wait() 无限阻塞，
        // 结果是 UWP 应用永远扫不到、永远搜不出来。现在最多等 3 秒就照常扫。
        try { FirstShown.Wait(TimeSpan.FromSeconds(3)); } catch { }
        var uwp = Scanners.ScanUwp(false);
        PublishEntries(custom, sm, reg, uwp);
        UwpScanned = true;
    }

    static void PublishEntries(List<Entry> custom, List<Entry> sm, List<Entry> reg, List<Entry> uwp)
    {
        var merged = EntryMerger.Merge(custom, sm, reg, uwp);
        _search.SetEntries(merged);
        if (_win != null)
            Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_SCAN_DONE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>窗口首次显示（幂等）：用于触发 UWP 扫描等延后任务。</summary>
    public static void NotifyFirstShown()
    {
        if (Interlocked.Exchange(ref _firstShown, 1) == 0)
            FirstShown.Set();
    }

    public static void AddFiles(string[] files)
    {
        bool added = false;
        foreach (var f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            bool isDir = Directory.Exists(f);
            if (ext != ".exe" && ext != ".lnk" && ext != ".url" && !isDir) continue;
            // 去重：重复拖入同一文件不应在 config.json 里累积重复条目
            if (_cfg.Custom.Any(c => c.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            string name = Path.GetFileNameWithoutExtension(f);
            if (ext == ".exe")
            {
                try
                {
                    var fi = FileVersionInfo.GetVersionInfo(f);
                    if (!string.IsNullOrEmpty(fi.FileDescription)) name = fi.FileDescription;
                }
                catch { }
            }
            _cfg.Custom.Add(new CustomEntry { Name = name, Path = f });
            added = true;
        }
        if (added)
        {
            ConfigStore.SaveConfig(_cfg);
            StartScan(true);
        }
    }

    public static void PickAndAddFile(IntPtr owner)
    {
        const int BufChars = 1024;
        IntPtr buf = Marshal.AllocHGlobal(BufChars * 2);
        try
        {
            Marshal.Copy(new byte[BufChars * 2], 0, buf, BufChars * 2);
            var ofn = new Win32.OPENFILENAMEW
            {
                lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAMEW>(),
                hwndOwner = owner,
                lpstrFilter = "程序\0*.exe;*.lnk;*.url\0所有文件\0*.*\0",
                lpstrFile = buf,
                nMaxFile = BufChars,
                lpstrTitle = "选择要添加的程序",
                Flags = Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_HIDEREADONLY,
            };
            if (Win32.GetOpenFileNameW(ref ofn))
            {
                string? f = Marshal.PtrToStringUni(buf);
                if (!string.IsNullOrEmpty(f)) AddFiles(new[] { f });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// 开机自启开关。以**注册表**为真相源（而不是 config.json）：
    ///   - 写注册表失败时必须回滚 UI 状态并提示，否则会出现「菜单已勾选、实际没写进去」；
    ///   - 写入后回读校验，因为被策略/安全软件拦截时 SetValue 不会抛异常，只是不生效。
    /// </summary>
    public static void ToggleAutoStart()
    {
        bool currentlyOn = AutoStart.IsActive();

        if (!currentlyOn)
        {
            // —— 开启 ——
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Tray.Notify("无法设置开机自启", "找不到当前程序文件路径。");
                return;
            }

            // 当前跑的是构建目录里的临时产物？先安装到稳定位置再指向它
            if (AutoStart.IsBuildOutput(exe))
            {
                var (ok, msg) = AutoStart.InstallLocal(exe);
                if (!ok)
                {
                    Tray.Notify("无法设置开机自启", "复制到本地目录失败：" + msg);
                    return;
                }
                exe = AutoStart.InstalledExe;
            }

            if (!AutoStart.TryEnable(exe))
            {
                // 写不进去：回滚状态 + 明确告知（不静默失败）
                _cfg.AutoStart = false;
                ConfigStore.SaveConfig(_cfg);
                Tray.Notify("开机自启设置失败",
                    "注册表写入被拒绝或被安全软件拦截。可改用菜单「安装到本地并设为开机自启」，或手动添加：\n" +
                    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run → MiniLaunch = \"" + exe + "\"");
                return;
            }

            _cfg.AutoStart = true;
            ConfigStore.SaveConfig(_cfg);
            Tray.Notify("已设置开机自启", exe);
            return;
        }

        // —— 关闭 ——
        if (!AutoStart.TryDisable())
        {
            Tray.Notify("关闭开机自启失败",
                "注册表项删除失败，可手动删除：HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 下的 MiniLaunch。");
            return;
        }
        _cfg.AutoStart = false;
        ConfigStore.SaveConfig(_cfg);
        Tray.Notify("已关闭开机自启", "");
    }

    /// <summary>开机自启自愈：让注册表项与用户意图、当前 exe 保持一致。</summary>
    static void HealAutoStart(bool configExists)
    {
        try
        {
            var r = AutoStart.Heal(_cfg.AutoStart, Environment.ProcessPath, configExists);
            switch (r)
            {
                case AutoStart.HealResult.Adopted:
                case AutoStart.HealResult.Repaired:
                    if (!_cfg.AutoStart) { _cfg.AutoStart = true; ConfigStore.SaveConfig(_cfg); }
                    break;
                case AutoStart.HealResult.Disabled:
                    if (_cfg.AutoStart) { _cfg.AutoStart = false; ConfigStore.SaveConfig(_cfg); }
                    break;
            }
            // 把自愈过程落到报告文件：用户不用点菜单就能看到「为什么没自启」
            try
            {
                string path = Path.Combine(Paths.Dir, "autostart-report.txt");
                File.WriteAllText(path, BuildAutoStartReport());
            }
            catch { }
        }
        catch { }
    }

    /// <summary>托盘菜单「安装到本地并设为开机自启」：复制到 %LOCALAPPDATA%\MiniLaunch 后指向它。</summary>
    public static void InstallLocalAndEnable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Tray.Notify("安装失败", "找不到当前程序文件路径。");
            return;
        }
        var (ok, msg) = AutoStart.InstallLocal(exe);
        if (!ok)
        {
            Tray.Notify("安装到本地失败", msg);
            return;
        }
        if (!AutoStart.TryEnable(AutoStart.InstalledExe))
        {
            Tray.Notify("开机自启设置失败", "程序已复制到本地，但注册表写入失败。");
            return;
        }
        _cfg.AutoStart = true;
        ConfigStore.SaveConfig(_cfg);
        Tray.Notify("已安装到本地并设为开机自启",
            AutoStart.InstalledExe + "\n本次运行仍是原位置，下次重启后由本地副本启动。");
    }

    /// <summary>开机自启诊断报告（写入 %APPDATA%\MiniLaunch\autostart-report.txt）。</summary>
    static string BuildAutoStartReport()
    {
        string? raw = AutoStart.ReadRawCommand();
        string? target = AutoStart.ReadTargetExe();
        bool fileOk = target != null && File.Exists(target);
        string? cur = Environment.ProcessPath;
        bool curBuild = cur != null && AutoStart.IsBuildOutput(cur);

        var sb = new StringBuilder();
        sb.AppendLine("【MiniLaunch 开机自启诊断】  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        sb.AppendLine("注册表项  HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        sb.AppendLine("  值       : " + (string.IsNullOrEmpty(raw) ? "(未设置)" : raw));
        sb.AppendLine("  目标文件 : " + (target ?? "(无)"));
        sb.AppendLine("  文件存在 : " + (fileOk ? "是" : "否   ★ 这就是开机没有自启的直接原因"));
        sb.AppendLine();
        sb.AppendLine("当前运行  " + (cur ?? "(未知)"));
        sb.AppendLine("  路径类型: " + (curBuild ? "构建目录（临时）★ 不适合做自启目标" : "稳定路径"));
        sb.AppendLine();
        sb.AppendLine("配置目录  " + Paths.Dir);
        sb.AppendLine("  config   : " + Paths.ConfigPath + "  存在=" + File.Exists(Paths.ConfigPath));
        sb.AppendLine("  APPDATA  : " + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        sb.AppendLine("  路径自检 : " + _startupDiag);
        sb.AppendLine();
        sb.AppendLine("热键      " + _cfg.Hotkey + " → " + (HotkeyOk ? "已注册" : "★ 注册失败（可能被输入法占用）"));
        sb.AppendLine("UWP 扫描  " + (UwpScanned ? "已完成" : "尚未完成"));
        sb.AppendLine("任务计划  " + (AutoStart.IsTaskRegistered() ? "已注册" : "未注册"));
        sb.AppendLine("稳定安装目录 " + AutoStart.InstallDir);
        sb.AppendLine();
        sb.AppendLine("--- 本次启动的自愈过程 ---");
        sb.AppendLine(AutoStart.LastHealDetail);
        sb.AppendLine();
        sb.AppendLine("建议：托盘菜单「安装到本地并设为开机自启」，把程序复制到 %LOCALAPPDATA%\\MiniLaunch 后再启用自启。");
        return sb.ToString();
    }

    /// <summary>托盘菜单「开机自启诊断…」：把注册表/文件/热键/UWP 扫描状态一次说清。</summary>
    public static void ShowAutoStartDiagnostics()
    {
        string report = BuildAutoStartReport();
        string reportPath = Path.Combine(Paths.Dir, "autostart-report.txt");
        try { File.WriteAllText(reportPath, report); } catch { }

        string brief =
            "注册表项：" + (string.IsNullOrEmpty(AutoStart.ReadRawCommand()) ? "(未设置)" : "已设置") + "\n" +
            "目标文件存在：" + (AutoStart.IsActive() ? "是" : "否  ← 没自启的原因") + "\n" +
            "当前路径：" + (Environment.ProcessPath != null && AutoStart.IsBuildOutput(Environment.ProcessPath) ? "构建目录（临时，不稳定）" : "稳定") + "\n" +
            "热键：" + (HotkeyOk ? "正常" : "注册失败") + "\n" +
            "UWP 扫描：" + (UwpScanned ? "已完成" : "未完成") + "\n\n" +
            "完整报告：" + reportPath;

        try
        {
            Win32.MessageBoxW(_win?.Hwnd ?? IntPtr.Zero, brief, "开机自启诊断",
                Win32.MB_OK | Win32.MB_ICONINFORMATION);
        }
        catch { }
    }

    /// <summary>注册「登录即启动」的任务计划（不需要管理员）。用于 Run 项被安全软件拦截时的替代方案。</summary>
    public static void RegisterLogonTask()
    {
        string exe = AutoStart.InstalledExe;
        if (!File.Exists(exe))
        {
            var cur = Environment.ProcessPath;
            if (string.IsNullOrEmpty(cur) || !File.Exists(cur)) { Tray.Notify("注册失败", "找不到可用的程序路径。"); return; }
            var (ok, msg) = AutoStart.InstallLocal(cur);
            if (!ok) { Tray.Notify("注册失败", msg); return; }
            exe = AutoStart.InstalledExe;
        }
        if (AutoStart.RegisterLogonTask(exe))
            Tray.Notify("已注册登录自启任务", exe);
        else
            Tray.Notify("注册任务计划失败", "schtasks 执行失败，可能被策略限制。");
    }

    // ================= 热键设置 =================
    /// <summary>打开热键录制窗（录制期间先注销旧热键，避免自我干扰）。</summary>
    public static void ShowHotkeySettings()
    {
        if (_win == null) return;
        if (HotkeyDialog.IsOpen) { Win32.SetForegroundWindow(HotkeyDialog.InstanceHwnd); return; }
        if (Win32.IsWindowVisible(_win.Hwnd)) _win.HideWin();
        Win32.UnregisterHotKey(_win.Hwnd, 1);
        HotkeyDialog.Show(TrySetHotkey, OnHotkeyDialogClosed);
    }

    /// <summary>尝试应用新热键；被占用则回滚旧热键并返回 false。</summary>
    static bool TrySetHotkey(string text)
    {
        if (_win == null) return false;
        var (mods, vk, ok) = HotkeyParser.Parse(text);
        if (!ok) return false;
        Win32.UnregisterHotKey(_win.Hwnd, 1);
        if (!Win32.RegisterHotKey(_win.Hwnd, 1, mods | Win32.MOD_NOREPEAT, vk))
        {
            RegisterCurrentHotkey();
            return false;
        }
        _cfg.Hotkey = text;
        ConfigStore.SaveConfig(_cfg);
        _win.RequestRepaint();
        Tray.Notify("热键已更新", text);
        return true;
    }

    static void OnHotkeyDialogClosed()
    {
        if (_win == null) return;
        RegisterCurrentHotkey();
        _win.RequestRepaint();
    }

    static void RegisterCurrentHotkey()
    {
        if (_win == null) return;
        Win32.UnregisterHotKey(_win.Hwnd, 1);
        var (mods, vk, ok) = HotkeyParser.Parse(_cfg.Hotkey);
        if (ok) Win32.RegisterHotKey(_win.Hwnd, 1, mods | Win32.MOD_NOREPEAT, vk);
        else Tray.Notify("热键配置无效", $"config.json 中的 \"{_cfg.Hotkey}\" 无法解析，请在托盘菜单重新设置。");
    }

    public static void OpenConfig()
    {
        try
        {
            var info = new Win32.SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFOW>(),
                lpFile = Paths.ConfigPath,
                nShow = Win32.SW_SHOWNORMAL,
            };
            Win32.ShellExecuteExW(ref info);
        }
        catch { }
    }
}
