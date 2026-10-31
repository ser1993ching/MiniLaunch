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
        {
            try
            {
                string exe = Environment.ProcessPath ?? "explorer.exe";
                var fi = new Win32.SHFILEINFOW();
                IntPtr r = Win32.SHGetFileInfoW(exe, 0, ref fi, (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(),
                    Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
                if (r != IntPtr.Zero) _icon = fi.hIcon;
            }
            catch { }
        }
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
        Win32.AppendMenuW(menu, Win32.MF_STRING, 101, "添加程序…");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 102, "重新扫描");
        Win32.AppendMenuW(menu, Win32.MF_STRING | (Program.Cfg.AutoStart ? Win32.MF_CHECKED : 0u), 103, "开机自启");
        Win32.AppendMenuW(menu, Win32.MF_STRING, 104, "打开配置文件");
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
            case 101: Program.PickAndAddFile(hwnd); break;
            case 102: Program.StartScan(true); break;
            case 103: Program.ToggleAutoStart(); break;
            case 104: Program.OpenConfig(); break;
            case 105: Win32.DestroyWindow(hwnd); break;
        }
    }
}
