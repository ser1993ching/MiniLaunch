using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

public sealed class MainWindow
{
    public const string ClassName = "MiniLaunchWnd";
    const int EditId = 1;
    const int VisibleRows = 8;

    // 基准尺寸（96 DPI）
    const int BaseW = 680, BasePad = 16, BaseEditH = 48, BaseRowH = 48, BaseIcon = 32, BaseGap = 10;

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static readonly Win32.SubclassProcDelegate s_editProc = EditProcStatic;
    static MainWindow? s_this;
    public static MainWindow? Instance => s_this;

    public IntPtr Hwnd;
    IntPtr _edit;

    readonly AppConfig _cfg;
    readonly SearchEngine _search;
    readonly IconCache _icons;
    readonly Action<Entry, bool> _onLaunch; // (条目, 是否管理员运行)
    readonly Action _rescan;
    readonly Action<string[]> _addFiles;

    Entry[] _results = Array.Empty<Entry>();
    int _sel, _offset = -1, _hover = -1;
    bool _trackMouse;

    float _scale = 1f;
    int _w, _pad, _editH, _rowH, _icon, _gap, _listTop, _h;
    IntPtr _fontMain, _fontSub, _fontEdit;
    IntPtr _brushBg, _brushHover, _brushSel, _brushEditBg;

    // 配色（uTools 风格深色，0x00BBGGRR）
    static readonly uint Bg = Win32.Rgb(0x25, 0x26, 0x2B);      // #25262B
    static readonly uint Hover = Win32.Rgb(0x2E, 0x30, 0x38);    // #2E3038
    static readonly uint Sel = Win32.Rgb(0x3B, 0x5B, 0xDB);      // #3B5BDB
    static readonly uint EditBg = Win32.Rgb(0x25, 0x26, 0x2B);
    static readonly uint TextMain = Win32.Rgb(0xF2, 0xF3, 0xF5);
    static readonly uint TextSub = Win32.Rgb(0x9B, 0x9E, 0xA8);

    MainWindow(AppConfig cfg, SearchEngine search, IconCache icons, Action<Entry, bool> onLaunch, Action rescan, Action<string[]> addFiles)
    {
        _cfg = cfg;
        _search = search;
        _icons = icons;
        _onLaunch = onLaunch;
        _rescan = rescan;
        _addFiles = addFiles;
    }

    public static MainWindow Create(AppConfig cfg, SearchEngine search, IconCache icons,
        Action<Entry, bool> onLaunch, Action rescan, Action<string[]> addFiles)
    {
        var self = new MainWindow(cfg, search, icons, onLaunch, rescan, addFiles);
        s_this = self;
        self.RecalcMetrics(1f);

        var hinst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = hinst,
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        ushort atom = Win32.RegisterClassW(ref wc);
        if (atom == 0) throw new InvalidOperationException("RegisterClassW 失败: " + Marshal.GetLastWin32Error());

        IntPtr hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW,
            ClassName, "", Win32.WS_POPUP,
            100, 100, self._w, self._h,
            IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowExW 失败: " + Marshal.GetLastWin32Error());
        self.Hwnd = hwnd;
        return self;
    }

    // ================= 显示/隐藏 =================
    public void ToggleShow()
    {
        if (Win32.IsWindowVisible(Hwnd)) HideWin();
        else ShowWin();
    }

    public void ShowWin()
    {
        Win32.GetCursorPos(out var pt);
        IntPtr mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        int waL = 0, waT = 0, waW = 1920, waH = 1080;
        if (Win32.GetMonitorInfoW(mon, ref mi))
        {
            waL = mi.rcWork.L; waT = mi.rcWork.T;
            waW = mi.rcWork.Width; waH = mi.rcWork.Height;
        }
        int x = waL + (waW - _w) / 2;
        int y = waT + (int)(waH * 0.32);
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE);

        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        ForceForeground();
        Win32.SetWindowTextW(_edit, "");
        Requery();
        if (Win32.SetFocus(_edit) == IntPtr.Zero) { /* 前台切换失败时下次点击仍有焦点 */ }
    }

    public void HideWin()
    {
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    void ForceForeground()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        uint t1 = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        uint t2 = Win32.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (t1 != 0 && t1 != t2) attached = Win32.AttachThreadInput(t2, t1, true);
            Win32.SetForegroundWindow(Hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(t2, t1, false);
        }
    }

    // ================= 查询 =================
    public void Requery()
    {
        var sb = new StringBuilder(512);
        Win32.GetWindowTextW(_edit, sb, 512);
        string text = sb.ToString();
        int max = Math.Max(_cfg.MaxResults, VisibleRows) * 3;
        _results = _search.Query(text, max).ToArray();
        _sel = 0;
        _offset = 0;
        _hover = -1;
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, true);
    }

    void MoveSel(int d)
    {
        if (_results.Length == 0) return;
        int ns = Math.Clamp(_sel + d, 0, _results.Length - 1);
        if (ns == _sel) return;
        _sel = ns;
        if (_sel < _offset) _offset = _sel;
        if (_sel >= _offset + VisibleRows) _offset = _sel - VisibleRows + 1;
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    void LaunchSelected()
    {
        if (_results.Length > 0 && _sel < _results.Length)
            _onLaunch(_results[_sel], false);
    }

    // ================= 布局 =================
    void RecalcMetrics(float scale)
    {
        _scale = scale;
        _w = Scale(BaseW);
        _pad = Scale(BasePad);
        _editH = Scale(BaseEditH);
        _rowH = Scale(BaseRowH);
        _icon = Scale(BaseIcon);
        _gap = Scale(BaseGap);
        _listTop = _pad + _editH + _gap;
        _h = _listTop + _rowH * VisibleRows + _pad;
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    void CreateFonts()
    {
        DeleteFonts();
        _fontMain = Win32.CreateFontW(-Scale(19), 0, 0, 0, Win32.FW_SEMIBOLD, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");
        _fontSub = Win32.CreateFontW(-Scale(13), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");
        _fontEdit = Win32.CreateFontW(-Scale(20), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");
        if (_edit != IntPtr.Zero)
            Win32.SendMessageW(_edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
    }

    void DeleteFonts()
    {
        if (_fontMain != IntPtr.Zero) { Win32.DeleteObject(_fontMain); _fontMain = IntPtr.Zero; }
        if (_fontSub != IntPtr.Zero) { Win32.DeleteObject(_fontSub); _fontSub = IntPtr.Zero; }
        if (_fontEdit != IntPtr.Zero) { Win32.DeleteObject(_fontEdit); _fontEdit = IntPtr.Zero; }
    }

    void CreateBrushes()
    {
        _brushBg = Win32.CreateSolidBrush(Bg);
        _brushHover = Win32.CreateSolidBrush(Hover);
        _brushSel = Win32.CreateSolidBrush(Sel);
        _brushEditBg = Win32.CreateSolidBrush(EditBg);
    }

    // ================= 窗口过程 =================
    static IntPtr WndProcStatic(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        var self = s_this;
        if (self == null) return Win32.DefWindowProcW(h, msg, w, l);
        return self.WndProc(h, msg, w, l);
    }

    IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Win32.WM_CREATE:
                OnCreate(h);
                return IntPtr.Zero;

            case Win32.WM_DPICHANGED:
            {
                float dpi = (ushort)Win32.LOWORD(w);
                RecalcMetrics(dpi / 96f);
                CreateFonts();
                LayoutEdit();
                var rect = Marshal.PtrToStructure<Win32.RECT>(l);
                Win32.SetWindowPos(h, IntPtr.Zero, rect.L, rect.T, rect.Width, rect.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                return IntPtr.Zero;
            }

            case Win32.WM_ERASEBKGND:
                return (IntPtr)1;

            case Win32.WM_PAINT:
                OnPaint();
                return IntPtr.Zero;

            case Win32.WM_CTLCOLOREDIT:
                if (w == _edit)
                {
                    Win32.SetTextColor(w, (int)TextMain);
                    Win32.SetBkColor(w, (int)EditBg);
                    return _brushEditBg;
                }
                break;

            case Win32.WM_ACTIVATE:
                if (Win32.LOWORD(w) == 0) // WA_INACTIVE
                {
                    uint fgThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
                    if (fgThread != Win32.GetCurrentThreadId())
                        HideWin();
                }
                return IntPtr.Zero;

            case Win32.WM_HOTKEY:
                if ((int)w == 1) ToggleShow();
                return IntPtr.Zero;

            case Win32.WM_COMMAND:
                if (Win32.LOWORD(w) == EditId && Win32.HIWORD(w) == Win32.EN_CHANGE)
                    Requery();
                return IntPtr.Zero;

            case Win32.WM_APP_SHOW:
                ShowWin();
                return IntPtr.Zero;

            case Win32.WM_APP_SCAN_DONE:
                if (Win32.IsWindowVisible(h)) Requery();
                return IntPtr.Zero;

            case Win32.WM_APP_TRAY:
                Tray.HandleTrayMessage(l, h);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
            {
                int row = HitRow(l);
                if (row >= 0 && row < _results.Length)
                {
                    _sel = row;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_LBUTTONUP:
            {
                int row = HitRow(l);
                if (row >= 0 && row < _results.Length && row == _sel)
                    LaunchSelected();
                return IntPtr.Zero;
            }

            case Win32.WM_RBUTTONUP:
            {
                int row = HitRow(l);
                if (row >= 0 && row < _results.Length)
                {
                    var pt = new Win32.POINT { X = Win32.LOWORD(l), Y = Win32.HIWORD(l) };
                    Win32.ClientToScreen(h, ref pt);
                    ItemMenu(row, pt.X, pt.Y);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_CONTEXTMENU:
                if (w == h)
                {
                    var pt = new Win32.POINT { X = Win32.LOWORD(l), Y = Win32.HIWORD(l) };
                    var client = pt;
                    Win32.ScreenToClient(h, ref client);
                    int row = HitRowAt(client.X, client.Y);
                    if (row >= 0 && row < _results.Length)
                        ItemMenu(row, pt.X, pt.Y);
                    return IntPtr.Zero;
                }
                break;

            case Win32.WM_MOUSEMOVE:
            {
                if (!_trackMouse)
                {
                    var tme = new Win32.TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                        dwFlags = Win32.TME_LEAVE,
                        hwndTrack = h,
                    };
                    Win32.TrackMouseEvent(ref tme);
                    _trackMouse = true;
                }
                int hv = HitRow(l);
                if (hv != _hover)
                {
                    _hover = hv;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_MOUSELEAVE:
                _trackMouse = false;
                if (_hover != -1) { _hover = -1; Win32.InvalidateRect(h, IntPtr.Zero, false); }
                return IntPtr.Zero;

            case Win32.WM_MOUSEWHEEL:
            {
                int delta = Win32.HIWORD(w);
                int maxOffset = Math.Max(0, _results.Length - VisibleRows);
                int no = Math.Clamp(_offset - Math.Sign(delta) * 3, 0, maxOffset);
                if (no != _offset) { _offset = no; Win32.InvalidateRect(h, IntPtr.Zero, false); }
                return IntPtr.Zero;
            }

            case Win32.WM_DROPFILES:
                OnDropFiles(w);
                return IntPtr.Zero;

            case Win32.WM_QUERYENDSESSION:
                break; // 用量由 Program 的 ProcessExit 钩子统一落盘

            case Win32.WM_DESTROY:
                DeleteFonts();
                foreach (var b in new[] { _brushBg, _brushHover, _brushSel, _brushEditBg })
                    if (b != IntPtr.Zero) Win32.DeleteObject(b);
                Win32.PostMessageW(h, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    void OnCreate(IntPtr h)
    {
        Hwnd = h;
        uint dpi = Win32.GetDpiForWindow(h);
        RecalcMetrics(dpi / 96f);
        CreateFonts();
        CreateBrushes();

        IntPtr hinst = Win32.GetModuleHandleW(null);
        _edit = Win32.CreateWindowExW(0, "EDIT", "",
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.ES_LEFT | Win32.ES_AUTOHSCROLL,
            _pad, _pad, _w - _pad * 2, _editH,
            h, (IntPtr)EditId, hinst, IntPtr.Zero);
        Win32.SetWindowSubclass(_edit, s_editProc, 1, IntPtr.Zero);
        Win32.SendMessageW(_edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        Win32.DragAcceptFiles(h, true);

        try
        {
            int pref = Win32.DWMWCP_ROUND;
            Win32.DwmSetWindowAttribute(h, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* Win10 无 DWM 圆角，忽略 */ }
    }

    void LayoutEdit()
    {
        if (_edit != IntPtr.Zero)
            Win32.MoveWindow(_edit, _pad, _pad, _w - _pad * 2, _editH, true);
    }

    void OnDropFiles(IntPtr hDrop)
    {
        uint count = Win32.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var sb = new StringBuilder(260);
            if (Win32.DragQueryFileW(hDrop, i, sb, 260) > 0)
                files.Add(sb.ToString());
        }
        Win32.DragFinish(hDrop);
        if (files.Count > 0) _addFiles(files.ToArray());
    }

    int HitRow(IntPtr l) => HitRowAt(Win32.LOWORD(l), Win32.HIWORD(l));

    int HitRowAt(int x, int y)
    {
        if (y < _listTop) return -1;
        int row = (y - _listTop) / _rowH;
        return row >= 0 && row < VisibleRows ? _offset + row : -1;
    }

    // ================= 条目菜单 =================
    void ItemMenu(int idx, int sx, int sy)
    {
        var e = _results[idx];
        var m = Win32.CreatePopupMenu();
        Win32.AppendMenuW(m, Win32.MF_STRING, (UIntPtr)1, "打开");
        Win32.AppendMenuW(m, Win32.MF_STRING, (UIntPtr)2, "以管理员身份运行");
        bool canReveal = e.RevealPath.Length > 0 && (File.Exists(e.RevealPath) || Directory.Exists(e.RevealPath));
        Win32.AppendMenuW(m, Win32.MF_STRING | (canReveal ? 0u : Win32.MF_GRAYED), (UIntPtr)3, "打开所在位置");
        Win32.AppendMenuW(m, Win32.MF_STRING, (UIntPtr)4, "复制路径");
        if (e.Source == EntrySource.Custom)
        {
            Win32.AppendMenuW(m, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32.AppendMenuW(m, Win32.MF_STRING, (UIntPtr)5, "从启动器删除");
        }
        Win32.SetForegroundWindow(Hwnd);
        int cmd = Win32.TrackPopupMenu(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_NONOTIFY,
            sx, sy, 0, Hwnd, IntPtr.Zero);
        Win32.PostMessageW(Hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case 1: _onLaunch(e, false); break;
            case 2: _onLaunch(e, true); break;
            case 3: Launcher.Reveal(e.RevealPath); break;
            case 4: Launcher.SetClipboardText(Hwnd, e.DisplayPath); break;
            case 5: RemoveCustom(e); break;
        }
    }

    void RemoveCustom(Entry e)
    {
        _cfg.Custom.RemoveAll(c =>
            c.Path.Equals(e.LaunchPath, StringComparison.OrdinalIgnoreCase) && c.Name == e.Name);
        ConfigStore.SaveConfig(_cfg);
        _rescan();
    }

    // ================= 绘制 =================
    void OnPaint()
    {
        IntPtr hdc = Win32.BeginPaint(Hwnd, out var ps);
        try
        {
            Win32.GetClientRect(Hwnd, out var rc);
            int cx = rc.Width, cy = rc.Height;
            if (cx <= 0 || cy <= 0) return;

            IntPtr mem = Win32.CreateCompatibleDC(hdc);
            IntPtr bmp = Win32.CreateCompatibleBitmap(hdc, cx, cy);
            IntPtr oldBmp = Win32.SelectObject(mem, bmp);

            var all = new Win32.RECT { L = 0, T = 0, R = cx, B = cy };
            Win32.FillRect(mem, ref all, _brushBg);

            Win32.SetBkMode(mem, Win32.TRANSPARENT);

            if (_results.Length == 0)
            {
                Win32.SelectObject(mem, _fontSub);
                Win32.SetTextColor(mem, (int)TextSub);
                var r = new Win32.RECT { L = 0, T = _listTop, R = cx, B = _listTop + _rowH };
                Win32.DrawTextW(mem, "未找到匹配的应用", -1, ref r,
                    Win32.DT_SINGLELINE | Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_NOPREFIX);
                // 左边距
            }
            else
            {
                int rows = Math.Min(VisibleRows, _results.Length - _offset);
                for (int i = 0; i < rows; i++)
                {
                    int idx = _offset + i;
                    var e = _results[idx];
                    int y = _listTop + i * _rowH;
                    var row = new Win32.RECT { L = _pad, T = y, R = cx - _pad, B = y + _rowH - _gap / 2 };

                    if (idx == _sel) Win32.FillRect(mem, ref row, _brushSel);
                    else if (idx == _hover) Win32.FillRect(mem, ref row, _brushHover);

                    // 图标
                    IntPtr hIcon = _icons.Get(e);
                    if (hIcon != IntPtr.Zero)
                        Win32.DrawIconEx(mem, row.L + _pad, y + (_rowH - _icon) / 2, hIcon, _icon, _icon, 0, IntPtr.Zero, Win32.DI_NORMAL);

                    // 主文本
                    int textX = row.L + _pad * 2 + _icon;
                    Win32.SelectObject(mem, _fontMain);
                    Win32.SetTextColor(mem, (int)TextMain);
                    var rName = new Win32.RECT { L = textX, T = y, R = row.R - _pad, B = y + _rowH };
                    Win32.DrawTextW(mem, e.Name, -1, ref rName,
                        Win32.DT_SINGLELINE | Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);

                    // 副文本（路径/AUMID）
                    string sub = Shorten(e.DisplayPath, 72);
                    if (sub.Length > 0)
                    {
                        Win32.SelectObject(mem, _fontSub);
                        Win32.SetTextColor(mem, (int)TextSub);
                        var rSub = new Win32.RECT { L = textX + Scale(150), T = y, R = row.R - _pad, B = y + _rowH };
                        Win32.DrawTextW(mem, sub, -1, ref rSub,
                            Win32.DT_SINGLELINE | Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
                    }
                }
            }

            Win32.BitBlt(hdc, 0, 0, cx, cy, mem, 0, 0, Win32.SRCCOPY);
            Win32.SelectObject(mem, oldBmp);
            Win32.DeleteObject(bmp);
            Win32.DeleteDC(mem);
        }
        finally
        {
            Win32.EndPaint(Hwnd, ref ps);
        }
    }

    static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        int start = s.Length - (max - 1);
        return "…" + s.Substring(start);
    }

    // ================= EDIT 子类化 =================
    static IntPtr EditProcStatic(IntPtr h, uint msg, IntPtr w, IntPtr l, IntPtr id, IntPtr data)
    {
        var self = s_this;
        if (self == null) return Win32.DefSubclassProc(h, msg, w, l);
        if (msg == Win32.WM_KEYDOWN)
        {
            int vk = (int)w & 0xFF;
            switch (vk)
            {
                case Win32.VK_UP: self.MoveSel(-1); return IntPtr.Zero;
                case Win32.VK_DOWN: self.MoveSel(1); return IntPtr.Zero;
                case Win32.VK_RETURN: self.LaunchSelected(); return IntPtr.Zero;
                case Win32.VK_ESCAPE: self.HideWin(); return IntPtr.Zero;
            }
        }
        return Win32.DefSubclassProc(h, msg, w, l);
    }
}
