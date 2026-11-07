using System.Runtime.InteropServices;

namespace MiniLaunch;

// 托盘图标与菜单
public static class Tray
{
    const uint TrayUid = 0x51;
    static IntPtr _hwnd;
    static bool _added;
    static IntPtr _icon;

    public static void Init(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var nid = MakeNid("MiniLaunch 启动器", showIcon: true);
        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref nid);
    }

    static Win32.NOTIFYICONDATAW MakeNid(string tip, bool showIcon)
    {
        if (_icon == IntPtr.Zero && showIcon)
            _icon = LoadAppIcon();
        var nid = new Win32.NOTIFYICONDATAW
        {
            hWnd = _hwnd,
            uID = TrayUid,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = Win32.WM_APP_TRAY,
            hIcon = _icon,
            szTip = tip,
        };
        nid.cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>();
        return nid;
    }

    /// <summary>取应用图标：exe 模块资源 → 内嵌 ico → exe 关联图标（兑底）。</summary>
    static IntPtr LoadAppIcon()
    {
        IntPtr h = AppIcon.Load();
        if (h != IntPtr.Zero) return h;
        try
        {
            string exe = Environment.ProcessPath ?? "explorer.exe";
            var fi = new Win32.SHFILEINFOW();
            IntPtr r = Win32.SHGetFileInfoW(exe, 0, ref fi, (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(),
                Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            if (r != IntPtr.Zero) return fi.hIcon;
        }
        catch { }
        return IntPtr.Zero;
    }

    public static void Destroy()
    {
        if (!_added) return;
        var n = new Win32.NOTIFYICONDATAW { hWnd = _hwnd, uID = TrayUid };
        n.cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>();
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref n);
        _added = false;
        if (_icon != IntPtr.Zero) { Win32.DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    public static void Notify(string title, string text)
    {
        if (!_added) return;
        var n = MakeNid("MiniLaunch 启动器", showIcon: false);
        n.uFlags |= Win32.NIF_INFO;
        n.szInfo = text;
        n.szInfoTitle = title;
        n.dwInfoFlags = Win32.NIIF_INFO;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref n);
    }

    public static void HandleTrayMessage(IntPtr lParam, IntPtr hwnd)
    {
        uint m = (uint)lParam.ToInt64() & 0xFFFF;
        switch (m)
        {
            case 0x0202: // WM_LBUTTONUP
            case 0x0203: // WM_LBUTTONDBLCLK
                MainWindow.Instance?.ShowWin();
                break;
            case 0x0205: // WM_RBUTTONUP
                ShowMenu(hwnd);
                break;
        }
    }

    static void ShowMenu(IntPtr hwnd)
    {
        var menu = Win32.CreatePopupMenu();
        Win32.AppendMenuW(menu, Win32.MF_STRING, 100, "显示主窗口");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 106, "设置热键…（" + Program.Cfg.Hotkey + "）");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 101, "添加程序…");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 102, "重新扫描");
        // 勾选状态以**注册表**为准（真相源），而不是 config.json：
        // 否则注册表项被清理工具删掉后，菜单仍显示已勾选，用户却一直找不到原因。
        Win32.AppendMenuW(menu, Win32.MF_STRING | (AutoStart.IsActive() ? Win32.MF_CHECKED : 0u), 103, "开机自启");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 104, "打开配置文件");
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
        Win32.AppendMenuW(menu, Win32.MF_STRING, 107, "开机自启诊断…");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 108, "安装到本地并设为开机自启");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 109, "注册登录自启任务（任务计划）");
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
        Win32.AppendMenuW(menu, Win32.MF_STRING, 105, "退出");

        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(hwnd);
        int cmd = Win32.TrackPopupMenu(menu,
            Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_NONOTIFY | Win32.TPM_LEFTALIGN,
            pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        Win32.PostMessageW(hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(menu);

        switch (cmd)
        {
            case 100: MainWindow.Instance?.ShowWin(); break;
            case 106: Program.ShowHotkeySettings(); break;
            case 101: Program.PickAndAddFile(hwnd); break;
            case 102: Program.StartScan(true); break;
            case 103: Program.ToggleAutoStart(); break;
            case 104: Program.OpenConfig(); break;
            case 107: Program.ShowAutoStartDiagnostics(); break;
            case 108: Program.InstallLocalAndEnable(); break;
            case 109: Program.RegisterLogonTask(); break;
            case 105: Win32.DestroyWindow(hwnd); break;
        }
    }
}
