using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public static AppConfig Cfg => _cfg;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args.Contains("--selftest"))
            return SelfTest.Run();

        Paths.EnsureDir();
        bool firstRun = !File.Exists(Paths.ConfigPath);
        _cfg = ConfigStore.LoadConfig();
        _usage = ConfigStore.LoadUsage();
        _search = new SearchEngine(_usage);
        _icons = new IconCache();

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

        Dpi.EnableAwareness();

        StartScan(false);

        var signalThread = new Thread(WaitShowSignal) { IsBackground = true };
        signalThread.Start();

        _win = MainWindow.Create(_cfg, _search, _icons, OnLaunch, () => StartScan(true), AddFiles);

        Tray.Init(_win.Hwnd);

        var (mods, vk, ok) = HotkeyParser.Parse(_cfg.Hotkey);
        bool hkOk = ok && Win32.RegisterHotKey(_win.Hwnd, 1, mods | Win32.MOD_NOREPEAT, vk);
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
            while (true)
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                if (ev.WaitOne() && _win != null)
                    Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
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
        var custom = Scanners.ScanCustom(_cfg);
        var sm = Scanners.ScanStartMenu();
        var reg = Scanners.ScanRegistry();
        var uwp = Scanners.ScanUwp(false);
        var merged = EntryMerger.Merge(custom, sm, reg, uwp);
        _search.SetEntries(merged);
        if (_win != null)
            Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_SCAN_DONE, IntPtr.Zero, IntPtr.Zero);
    }

    public static void AddFiles(string[] files)
    {
        bool added = false;
        foreach (var f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            bool isDir = Directory.Exists(f);
            if (ext != ".exe" && ext != ".lnk" && ext != ".url" && !isDir) continue;
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

    public static void ToggleAutoStart()
    {
        _cfg.AutoStart = !_cfg.AutoStart;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_cfg.AutoStart)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) k.SetValue("MiniLaunch", "\"" + exe + "\"");
            }
            else
            {
                k.DeleteValue("MiniLaunch", throwOnMissingValue: false);
            }
        }
        catch { }
        ConfigStore.SaveConfig(_cfg);
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
