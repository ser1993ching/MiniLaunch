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
    /// <summary>扫描进行中标志；扫描期间又来的请求记在 _scanPending 里，扫完立刻再扫一轮。</summary>
    static int _scanning, _scanPending;

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

        // 命名事件必须在**主线程**先建好再交给后台线程：
        // 第二个实例 Set() 时若本进程还没 Create，信号会直接丢掉
        // （表现为「双击 exe 没反应」一次，只能再双击一次）。
        var showEvent = CreateShowEvent();
        var signalThread = new Thread(() => WaitShowSignal(showEvent)) { IsBackground = true };
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

        // 自启诊断报告必须晚于热键注册（也晚于 Tray.Init）：报告里含「热键是否注册成功」，
        // 早写的话这两个字段永远是 false，报告会一直误报「★ 热键注册失败」。
        WriteAutoStartReport();

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
        // 名称输入窗预览（调试/截图用）：带「已添加的文件夹」标签行，与真实入口一致
        if (args.Contains("--name-dialog"))
        {
            _win.PreviewMode = true;
            NameDialog.Show("添加文件夹", @"E:\code\启动器", "启动器", (n, p) => true,
                () => new List<CustomEntry>
                {
                    new() { Name = "启动器", Path = @"E:\code\启动器", IsFolder = true },
                    new() { Name = "电磁计算", Path = @"D:\Desktop\电磁计算", IsFolder = true },
                    new() { Name = "Moonglade-blog", Path = @"E:\code\Moonglade-blog", IsFolder = true },
                    new() { Name = "工作日报", Path = @"D:\工作日报", IsFolder = true },
                    new() { Name = "设计稿", Path = @"D:\设计稿", IsFolder = true },
                    new() { Name = "Downloads", Path = @"C:\Users\x\Downloads", IsFolder = true },
                    new() { Name = "源码归档", Path = @"E:\src", IsFolder = true },
                    new() { Name = "截图", Path = @"D:\截图", IsFolder = true },
                    new() { Name = "会议记录", Path = @"D:\会议记录", IsFolder = true },
                    new() { Name = "合同模版", Path = @"D:\合同模版", IsFolder = true },
                    new() { Name = "固件包", Path = @"E:\firmware", IsFolder = true },
                    new() { Name = "虚拟机镜像", Path = @"E:\vm", IsFolder = true },
                    new() { Name = "财务 2026", Path = @"D:\财务 2026", IsFolder = true },
                    new() { Name = "临时文件", Path = @"D:\tmp", IsFolder = true },
                },
                _ => { });
        }
        // 重命名窗预览（不带标签行的紧凑版）
        if (args.Contains("--name-dialog-plain"))
        {
            _win.PreviewMode = true;
            NameDialog.Show("重命名", @"D:\Desktop\电磁计算", "电磁计算", (n, p) => true);
        }
        // 标签行溢出预览：文件夹多到 4 行放不下，验证「另有 N 个未显示」（调试/截图用）
        if (args.Contains("--name-dialog-many"))
        {
            _win.PreviewMode = true;
            NameDialog.Show("添加文件夹", @"E:\code\启动器", "启动器", (n, p) => true,
                () =>
                {
                    var list = new List<CustomEntry>();
                    for (int i = 1; i <= 30; i++)
                        list.Add(new CustomEntry { Name = "文件夹 " + i.ToString("00"), Path = @"D:\demo\folder" + i, IsFolder = true });
                    return list;
                },
                _ => { });
        }
        // 网站编辑窗预览（调试/截图用）
        if (args.Contains("--web-dialog"))
        {
            _win.PreviewMode = true;
            WebDialog.Show("添加网站", "百度", "https://www.baidu.com",
                () => new List<WebSiteEntry>
                {
                    new WebSiteEntry { Name = "百度", Url = "https://www.baidu.com" },
                    new WebSiteEntry { Name = "GitHub", Url = "https://github.com" },
                },
                (n, u, t) => true,
                url => { });
        }
        // 文件夹选择对话框预览（调试用）：选完直接进名称输入窗
        if (args.Contains("--pick-folder"))
        {
            _win.PreviewMode = true;
            // 延后到消息循环起来之后再弹（SHBrowseForFolder 是模态框，需要消息泵）
            Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_PICK_FOLDER, IntPtr.Zero, IntPtr.Zero);
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

    static EventWaitHandle? CreateShowEvent()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch { return null; }
    }

    /// <summary>后台等「第二个实例让我显示」的信号（句柄由主线程创建后传入，只建一次）。</summary>
    static void WaitShowSignal(EventWaitHandle? ev)
    {
        if (ev == null) return;
        try
        {
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
        // 提权（UAC）时 ShellExecuteEx 会阻塞调用线程直到用户确认：放到线程池，
        // 否则 UI 线程（消息循环）被卡住——热键、托盘、重绘全部无响应。
        // 用量先乐观记一次，避免后台线程去碰 _usage（UI 线程也在写）。
        if (runAs || e.RunAs)
        {
            _usage.Bump(e.UniqueKey);
            ConfigStore.SaveUsage(_usage);
            ThreadPool.QueueUserWorkItem(_ => Launcher.Run(e, runAs));
        }
        else if (Launcher.Run(e, false))
        {
            _usage.Bump(e.UniqueKey);
            ConfigStore.SaveUsage(_usage);
        }
        _win?.HideWin();
    }

    public static void StartScan(bool foreground)
    {
        // 扫描进行中又来一次请求（新增 / 删除 / 改名 / 拖放都会走这里）：**不能直接丢弃**。
        // 正在跑的扫描用的是它开始时的配置快照，丢弃请求的后果是：
        //   · 刚加的条目不会出现在列表里（要重启才看得到）；
        //   · 刚删的条目被旧快照 publish 回去（删了还在、还能回车启动）。
        // 改成记账，扫完立刻再扫一轮。
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _scanPending, 1);
            return;
        }

        var t = new Thread(() =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _scanPending, 0);
                    try { DoScan(); }
                    // 兑底：扫描线程的任何异常都不能带崩进程（列出错误便于诊断）
                    catch (Exception ex) { NativeOut.WriteLine("扫描线程异常: " + ex.GetType().Name + ": " + ex.Message); }
                }
                while (Volatile.Read(ref _scanPending) != 0);
            }
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
        // 第一阶段（快，约 30ms）：自定义 + 网站 + 开始菜单 + 注册表 → 先发布，窗口不必等 UWP
        var custom = Scanners.ScanCustom(_cfg);
        var web = Scanners.ScanWebsites(_cfg);
        var sm = Scanners.ScanStartMenu();
        var reg = Scanners.ScanRegistry();
        PublishEntries(custom, sm, reg, new List<Entry>(), web);

        // 第二阶段（慢，首次要 1.3s 拉 powershell）：等「窗口首次显示」或 3 秒超时（先到者触发）。
        // 开机自启常驻时窗口可能永远不显示——此前用 FirstShown.Wait() 无限阻塞，
        // 结果是 UWP 应用永远扫不到、永远搜不出来。现在最多等 3 秒就照常扫。
        try { FirstShown.Wait(TimeSpan.FromSeconds(3)); } catch { }
        var uwp = Scanners.ScanUwp(false);
        PublishEntries(custom, sm, reg, uwp, web, final: true);
        UwpScanned = true;
        WriteAutoStartReport();   // UWP 状态已定，诊断报告重写一遍（否则永远是「尚未完成」）
    }

    static void PublishEntries(List<Entry> custom, List<Entry> sm, List<Entry> reg, List<Entry> uwp,
        List<Entry>? web = null, bool final = false)
    {
        var merged = EntryMerger.Merge(custom, sm, reg, uwp, web);
        _search.SetEntries(merged);
        // 用量表只增不减会越积越多（卸载/改名的条目永远留着记录）。
        // 只在**完整扫描结束后**裁剪：第一阶段还没有 UWP 条目，早裁会把 UWP 的用量清空。
        if (final) PruneUsage(merged);
        if (_win != null)
            Win32.PostMessageW(_win.Hwnd, Win32.WM_APP_SCAN_DONE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>剔除不再存在的条目的用量记录（扫描线程调用；UsageDb 内部有锁）。</summary>
    static void PruneUsage(List<Entry> merged)
    {
        var keep = new HashSet<string>(merged.Count);
        foreach (var e in merged) keep.Add(e.UniqueKey);
        // 只清理「已经不在条目里」且「30 天内没用过」的记录：
        // 某次扫描少扫了一大批（权限/临时错误）时不会把整份使用历史抹掉。
        long before = DateTime.UtcNow.Ticks - TimeSpan.FromDays(30).Ticks;
        if (_usage.PruneTo(keep, before) > 0) ConfigStore.SaveUsage(_usage);
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
            _cfg.Custom.Add(new CustomEntry { Name = name, Path = f, IsFolder = isDir });
            added = true;
        }
        if (added)
        {
            ConfigStore.SaveConfig(_cfg);
            StartScan(true);
        }
    }

    // ================= 文件夹快捷入口 =================
    /// <summary>托盘菜单「添加文件夹…」：选目录 → 起名 → 写入 config.json。
    /// 名称窗底部会列出已添加的文件夹（点标签=改成那一条，点 × =删除）。</summary>
    public static void PickAndAddFolder(IntPtr owner)
    {
        // 不把主窗口当对话框的 owner：选择框跑在另一个（STA）线程上，而主窗口在失焦时会
        // 自己隐藏（WM_ACTIVATE → HideWin），被拥有的对话框会跟着消失 → 用户看不到选目录窗口。
        string? dir = FolderPicker.Pick(IntPtr.Zero, "选择要快速进入的文件夹");
        if (string.IsNullOrEmpty(dir)) return;
        NameDialog.Show("添加文件夹", dir, DefaultFolderName(dir),
            (name, path) => AddFolder(name, path),
            FolderEntries, RemoveFolder);
    }

    /// <summary>
    /// 已添加的文件夹快捷入口快照（供名称窗底部标签行展示）。
    /// 判定与 Scanners.ScanCustom 一致：显式 IsFolder，或路径确实是存在的目录（兼容旧配置）。
    /// </summary>
    public static List<CustomEntry> FolderEntries()
    {
        var list = new List<CustomEntry>();
        foreach (var c in _cfg.Custom)
        {
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            bool isFolder;
            try { isFolder = c.IsFolder || Directory.Exists(c.Path); }
            catch { isFolder = c.IsFolder; }
            if (!isFolder) continue;
            list.Add(new CustomEntry { Name = c.Name, Path = c.Path, IsFolder = true });
        }
        return list;
    }

    /// <summary>按路径删除一个文件夹快捷入口（名称窗标签行上的 ×）。</summary>
    public static void RemoveFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        int n = _cfg.Custom.RemoveAll(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (n == 0) return;
        ConfigStore.SaveConfig(_cfg);
        StartScan(true);
    }

    /// <summary>目录默认显示名：取目录名本身（根目录则用路径）。</summary>
    static string DefaultFolderName(string dir)
    {
        try
        {
            string n = new DirectoryInfo(dir.TrimEnd('\\')).Name;
            return string.IsNullOrEmpty(n) ? dir : n;
        }
        catch { return dir; }
    }

    /// <summary>写入一个文件夹快捷条目；同一路径已存在时只更新名称。</summary>
    public static bool AddFolder(string name, string path)
    {
        path = path.Trim();
        if (path.Length == 0) return false;
        bool changed = false;
        foreach (var c in _cfg.Custom)
        {
            if (!c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
            c.Name = name;
            c.IsFolder = true;
            changed = true;
            break;
        }
        if (!changed)
            _cfg.Custom.Add(new CustomEntry { Name = name, Path = path, IsFolder = true });
        ConfigStore.SaveConfig(_cfg);
        StartScan(true);
        return true;
    }

    /// <summary>重命名自定义条目（右键菜单「重命名…」；不显示文件夹标签行，只改这一条）。</summary>
    public static void RenameCustom(Entry e)
    {
        NameDialog.Show("重命名", e.DisplayPath, e.Name, (name, _) =>
        {
            foreach (var c in _cfg.Custom)
            {
                if (!c.Path.Equals(e.LaunchPath, StringComparison.OrdinalIgnoreCase)) continue;
                c.Name = name;
                ConfigStore.SaveConfig(_cfg);
                // 用量键是「来源:小写名」：改名会让历史用量换键 → 排序立刻跌到谷底。
                // 把旧键的记录搬到新键上，改名不影响「常用」排序。
                MigrateUsageKey(e.UniqueKey, EntrySource.Custom + ":" + Entry.NormName(name));
                StartScan(true);
                return true;
            }
            return false;
        });
    }

    /// <summary>把用量记录从一个键搬到另一个键（重命名后保持「常用」排序）。</summary>
    static void MigrateUsageKey(string oldKey, string newKey)
    {
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;
        if (_usage.Move(oldKey, newKey)) ConfigStore.SaveUsage(_usage);
    }

    // ================= 网站快捷入口 =================
    /// <summary>托盘菜单「添加网站…」：弹出编辑窗，确认后写入 config.json。</summary>
    public static void PickAndAddWebSite(IntPtr owner)
    {
        if (WebDialog.IsOpen) return;
        WebDialog.Show("添加网站", "", "",
            () => _cfg.Websites ??= new List<WebSiteEntry>(),
            (name, url, target) => AddWebSite(name, url, target),
            url => RemoveWebSite(url));
    }

    /// <summary>新增（或更新）一个网站快捷入口。oldUrl 非空时表示「编辑」该条目。</summary>
    public static bool AddWebSite(string name, string url, string oldUrl = "")
    {
        _cfg.Websites ??= new List<WebSiteEntry>();
        if (!WebSites.Upsert(_cfg.Websites, name, url, oldUrl)) return false;
        ConfigStore.SaveConfig(_cfg);
        StartScan(true);
        return true;
    }

    /// <summary>编辑已有的网站快捷入口（右键菜单「编辑网站…」）。</summary>
    public static void EditWebSite(Entry e)
    {
        if (!e.IsWeb) return;
        WebDialog.Show("编辑网站", e.Name, e.LaunchPath,
            () => _cfg.Websites ??= new List<WebSiteEntry>(),
            (name, url, target) => AddWebSite(name, url,
                string.IsNullOrEmpty(target) ? e.LaunchPath : target),
            url => RemoveWebSite(url));
    }

    /// <summary>删除一个网站快捷入口（右键菜单「删除网站」）。</summary>
    public static void RemoveWebSite(string url)
    {
        _cfg.Websites ??= new List<WebSiteEntry>();
        WebSites.Remove(_cfg.Websites, url);
        ConfigStore.SaveConfig(_cfg);
        StartScan(true);
    }

    /// <summary>托盘菜单「添加程序…」：现代资源管理器风格文件选择器（STA 线程，见 FolderPicker）。</summary>
    public static void PickAndAddFile(IntPtr owner)
    {
        // 不把主窗口当 owner：选择框跑在另一个（STA）线程上，主窗口失焦时会自己隐藏，
        // 被拥有的对话框会跟着消失 → 用户看不到选择窗口（同 PickAndAddFolder）。
        string? f = FolderPicker.PickFile(IntPtr.Zero, "选择要添加的程序");
        if (!string.IsNullOrEmpty(f)) AddFiles(new[] { f! });
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
                    "注册表写入被拒绝或被安全软件拦截。可手动添加：\n" +
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
            // 自愈过程落到报告文件：用户不用点菜单就能看到「为什么没自启」。
            // 注意：写盘放到热键注册之后（WriteAutoStartReport），这里只算不写。
        }
        catch { }
    }

    /// <summary>
    /// 把自启诊断报告写到 %APPDATA%\MiniLaunch\autostart-report.txt（后台线程，不占启动路径）。
    /// 必须晚于热键注册，否则报告里的「热键 → 已注册/注册失败」永远是失败。
    /// </summary>
    static void WriteAutoStartReport()
    {
        var t = new Thread(() =>
        {
            try
            {
                string path = Path.Combine(Paths.Dir, "autostart-report.txt");
                File.WriteAllText(path, BuildAutoStartReport());
            }
            catch { }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.Report",
            Priority = ThreadPriority.Lowest,
        };
        t.Start();
    }

    /// <summary>开机自启自愈报告（每次启动写一份到 %APPDATA%\MiniLaunch\autostart-report.txt）。</summary>
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
        sb.AppendLine("稳定安装目录 " + AutoStart.InstallDir);
        sb.AppendLine();
        sb.AppendLine("--- 本次启动的自愈过程 ---");
        sb.AppendLine(AutoStart.LastHealDetail);
        sb.AppendLine();
        sb.AppendLine("建议：把程序放在稳定位置（如 %LOCALAPPDATA%\\MiniLaunch）再启用开机自启。");
        return sb.ToString();
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
