using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

public sealed class MainWindow
{
    public const string ClassName = "MiniLaunchWnd";
    const int EditId = 1;
    const int VisibleRows = 8;

    // 基准尺寸（96 DPI）
    const int BaseW = 720, BasePad = 16, BaseRowH = 54, BaseFooterH = 38, BaseIcon = 34;
    const int BaseSearchH = 54, BaseRadius = 12, BaseSearchRadius = 12;

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

    /// <summary>预览模式（--preview 启动）：不因失焦自动隐藏，便于截图与界面调试。</summary>
    public bool PreviewMode;

    float _scale = 1f;
    int _w, _h, _pad, _rowH, _icon, _radius;
    int _searchTop, _searchH, _dividerY, _listTop, _footerY, _footerH;
    IntPtr _fontMain, _fontSub, _fontEdit, _fontSmall, _fontKey;
    IntPtr _appIcon;
    IntPtr _brushHover, _brushSel, _brushEditBg, _brushCap, _brushTrack, _brushThumb, _brushBorder;

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
        int y = waT + (int)(waH * 0.30);
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

    /// <summary>外部状态变化（如热键改动）后请求重绘，用于刷新底栏提示。</summary>
    public void RequestRepaint()
    {
        if (Hwnd != IntPtr.Zero) Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
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
    /// <summary>调试/预览用：直接设入搜索词（会触发 EN_CHANGE → Requery）。</summary>
    public void SetQuery(string text)
    {
        if (_edit == IntPtr.Zero || string.IsNullOrEmpty(text)) return;
        Win32.SetWindowTextW(_edit, text);
        Win32.SendMessageW(_edit, Win32.EM_SETSEL, (IntPtr)text.Length, (IntPtr)text.Length);
    }

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
        _pad = Scale(BasePad);
        _w = Scale(BaseW);
        _rowH = Scale(BaseRowH);
        _icon = Scale(BaseIcon);
        _radius = Scale(BaseRadius);
        _searchTop = _pad;
        _searchH = Scale(BaseSearchH);
        _dividerY = _searchTop + _searchH + Scale(10);
        _listTop = _dividerY + Scale(8);
        _footerH = Scale(BaseFooterH);
        _footerY = _listTop + _rowH * VisibleRows + Scale(4);
        _h = _footerY + _footerH + Scale(6);
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    static IntPtr MakeFont(int px, int weight, float scale) =>
        Win32.CreateFontW(-(int)Math.Round(px * scale), 0, 0, 0, weight, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");

    void CreateFonts()
    {
        DeleteFonts();
        _fontMain = MakeFont(16, Win32.FW_SEMIBOLD, _scale);
        _fontSub = MakeFont(12, Win32.FW_NORMAL, _scale);
        _fontEdit = MakeFont(19, Win32.FW_NORMAL, _scale);
        _fontSmall = MakeFont(11, Win32.FW_NORMAL, _scale);
        _fontKey = MakeFont(11, Win32.FW_SEMIBOLD, _scale);
        if (_edit != IntPtr.Zero)
            Win32.SendMessageW(_edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
    }

    void DeleteFonts()
    {
        foreach (var f in new[] { _fontMain, _fontSub, _fontEdit, _fontSmall, _fontKey })
            if (f != IntPtr.Zero) Win32.DeleteObject(f);
        _fontMain = _fontSub = _fontEdit = _fontSmall = _fontKey = IntPtr.Zero;
    }

    void CreateBrushes()
    {
        _brushHover = Win32.CreateSolidBrush(Theme.Hover);
        _brushSel = Win32.CreateSolidBrush(Theme.SelBottom);
        _brushEditBg = Win32.CreateSolidBrush(Theme.Surface);
        _brushCap = Win32.CreateSolidBrush(Theme.KeyCapBg);
        _brushTrack = Win32.CreateSolidBrush(Theme.Divider);
        _brushThumb = Win32.CreateSolidBrush(Theme.Thumb);
        _brushBorder = Win32.CreateSolidBrush(Theme.SurfaceBorder);
    }

    IntPtr[] Brushes => new[] { _brushHover, _brushSel, _brushEditBg, _brushCap, _brushTrack, _brushThumb, _brushBorder };

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
                // 注意：wParam 是 HDC，lParam 才是指控件句柄
                if (l == _edit)
                {
                    Win32.SetTextColor(w, (int)Theme.TextMain);
                    Win32.SetBkColor(w, (int)Theme.Surface);
                    return _brushEditBg;
                }
                break;

            case Win32.WM_ACTIVATE:
                if (Win32.LOWORD(w) == 0 && !PreviewMode) // WA_INACTIVE
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
                foreach (var b in Brushes)
                    if (b != IntPtr.Zero) Win32.DeleteObject(b);
                if (_appIcon != IntPtr.Zero) { Win32.DestroyIcon(_appIcon); _appIcon = IntPtr.Zero; }
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    void OnCreate(IntPtr h)
    {
        Hwnd = h;
        uint dpi = Dpi.ForWindow(h);
        RecalcMetrics(dpi / 96f);
        CreateFonts();
        CreateBrushes();

        // 窗口图标（Alt+Tab / 任务栏预览）：与托盘同一来源
        _appIcon = AppIcon.Load();
        if (_appIcon != IntPtr.Zero)
        {
            Win32.SendMessageW(h, Win32.WM_SETICON, IntPtr.Zero, _appIcon);   // ICON_SMALL
            Win32.SendMessageW(h, Win32.WM_SETICON, (IntPtr)1, _appIcon);      // ICON_BIG
        }

        IntPtr hinst = Win32.GetModuleHandleW(null);
        // 搜索框：胶囊形 EDIT，左侧留出放大镜图标
        _edit = Win32.CreateWindowExW(0, "EDIT", "",
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.ES_LEFT | Win32.ES_AUTOHSCROLL,
            0, 0, 10, 10,
            h, (IntPtr)EditId, hinst, IntPtr.Zero);
        Win32.SetWindowSubclass(_edit, s_editProc, 1, IntPtr.Zero);
        Win32.SendMessageW(_edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        Win32.SendMessageW(_edit, Win32.EM_SETCUEBANNER, (IntPtr)1, Marshal.StringToHGlobalUni("搜索应用、文件或命令…"));
        LayoutEdit();
        Win32.DragAcceptFiles(h, true);

        Theme.ApplyModernFrame(h);
    }

    // 搜索胶囊的外框矩形（与 EDIT 位置共享同一套几何）
    Win32.RECT SearchPill(int cx)
    {
        int l = _pad + Scale(30);
        int t = _searchTop + Scale(4);
        return new Win32.RECT { L = l, T = t, R = cx - _pad, B = _searchTop + _searchH - Scale(4) };
    }

    void LayoutEdit()
    {
        if (_edit == IntPtr.Zero) return;
        var outer = SearchPill(_w);
        int l = outer.L + Scale(14), w = outer.Width - Scale(28);
        // 单行 EDIT 不支持 EM_SETRECT（那是多行控件的接口），只能把控件本身摆正：
        // 文字在 EDIT 客户区里贴近顶部（约 3px），所以控件高度取行高、整体下移，
        // 让文字的视觉中心落在胶囊中心上（否则文字会明显偏上）。
        int h = Scale(30);
        int t = (outer.T + outer.B) / 2 - Scale(13);
        Win32.MoveWindow(_edit, l, t, w, h, true);
        // 文本左右内边距
        int mg = Scale(12);
        Win32.SendMessageW(_edit, Win32.EM_SETMARGINS,
            (IntPtr)(Win32.EC_LEFTMARGIN | Win32.EC_RIGHTMARGIN), (IntPtr)((mg << 16) | (mg & 0xFFFF)));
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
        if (y < _listTop || y >= _listTop + _rowH * VisibleRows) return -1;
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

            DrawAll(mem, cx, cy);

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

    void DrawAll(IntPtr hdc, int cx, int cy)
    {
        var all = new Win32.RECT { L = 0, T = 0, R = cx, B = cy };
        Theme.GradientV(hdc, all, Theme.BgTop, Theme.BgBottom);
        Win32.SetBkMode(hdc, Win32.TRANSPARENT);

        DrawSearchBand(hdc, cx);
        DrawResults(hdc, cx);
        DrawFooter(hdc, cx);
    }

    // 顶部搜索区：放大镜 + 胶囊形搜索框（框体是 EDIT 子窗口，此处只画外圈与图标）
    void DrawSearchBand(IntPtr hdc, int cx)
    {
        // 放大镜
        int gs = Scale(17);
        int gx = _pad + Scale(6);
        int gy = _searchTop + (_searchH - gs) / 2;
        Theme.DrawMagnifier(hdc, gx, gy, gs, Theme.TextDim, Math.Max(1, Scale(2)));

        // 搜索框外框：1px 细边 + 圆角，内部由 EDIT 自己填充
        var outer = SearchPill(cx);
        int r = Scale(BaseSearchRadius);
        var inner = new Win32.RECT { L = outer.L + 1, T = outer.T + 1, R = outer.R - 1, B = outer.B - 1 };
        Theme.FillRoundSolid(hdc, outer, r, _brushBorder);
        Theme.FillRoundSolid(hdc, inner, Math.Max(2, r - 1), _brushEditBg);

        // 分隔线
        Theme.HLine(hdc, _pad + Scale(4), cx - _pad - Scale(4), _dividerY, Theme.Divider);
    }

    void DrawResults(IntPtr hdc, int cx)
    {
        if (_results.Length == 0)
        {
            Win32.SelectObject(hdc, _fontMain);
            Win32.SetTextColor(hdc, (int)Theme.TextSub);
            int y = _listTop + _rowH * 2;
            var r1 = new Win32.RECT { L = _pad, T = y, R = cx - _pad, B = y + Scale(26) };
            Win32.DrawTextW(hdc, "没有找到匹配的项目", -1, ref r1, Theme.AlignCenter);

            Win32.SelectObject(hdc, _fontSub);
            Win32.SetTextColor(hdc, (int)Theme.TextDim);
            var r2 = new Win32.RECT { L = _pad, T = y + Scale(28), R = cx - _pad, B = y + Scale(50) };
            Win32.DrawTextW(hdc, "试试拼音首字母（如 wx 找微信），或把程序拖进窗口添加", -1, ref r2, Theme.AlignCenter);
            return;
        }

        int rows = Math.Min(VisibleRows, _results.Length - _offset);
        for (int i = 0; i < rows; i++)
        {
            int idx = _offset + i;
            var e = _results[idx];
            bool selected = idx == _sel;
            bool hovered = !selected && idx == _hover;
            int y = _listTop + i * _rowH;
            var row = new Win32.RECT
            {
                L = _pad,
                T = y + Scale(2),
                R = cx - _pad - Scale(10),
                B = y + _rowH - Scale(2),
            };

            if (selected)
                Theme.FillRoundGradient(hdc, row, _radius, Theme.SelTop, Theme.SelBottom);
            else if (hovered)
                Theme.FillRoundSolid(hdc, row, _radius, _brushHover);

            // 图标
            IntPtr hIcon = _icons.Get(e);
            int iconX = row.L + Scale(12);
            int iconY = y + (_rowH - _icon) / 2;
            if (hIcon != IntPtr.Zero)
                Win32.DrawIconEx(hdc, iconX, iconY, hIcon, _icon, _icon, 0, IntPtr.Zero, Win32.DI_NORMAL);

            int textX = iconX + _icon + Scale(12);
            int textR = row.R - Scale(10);

            // 管理员标记（右侧，先算宽度再决定标题可用宽度）
            if (e.RunAs)
            {
                Win32.SelectObject(hdc, _fontSmall);
                Win32.SetTextColor(hdc, (int)(selected ? Theme.TextOnSelSub : Theme.Warn));
                var rTag = new Win32.RECT { L = textR - Scale(52), T = y, R = textR, B = y + _rowH };
                Win32.DrawTextW(hdc, "管理员", -1, ref rTag, Theme.AlignRight);
                textR -= Scale(58);
            }

            // 主标题
            Win32.SelectObject(hdc, _fontMain);
            Win32.SetTextColor(hdc, (int)(selected ? Theme.TextOnSel : Theme.TextMain));
            var rName = new Win32.RECT { L = textX, T = y + Scale(8), R = textR, B = y + Scale(8) + Scale(22) };
            Win32.DrawTextW(hdc, e.Name, -1, ref rName, Theme.AlignLeft);

            // 副标题（路径 / AUMID）
            string sub = Shorten(e.DisplayPath, 76);
            if (sub.Length > 0)
            {
                Win32.SelectObject(hdc, _fontSub);
                Win32.SetTextColor(hdc, (int)(selected ? Theme.TextOnSelSub : Theme.TextSub));
                var rSub = new Win32.RECT { L = textX, T = y + Scale(29), R = textR, B = y + Scale(29) + Scale(16) };
                Win32.DrawTextW(hdc, sub, -1, ref rSub, Theme.AlignLeft);
            }
        }

        DrawScrollbar(hdc, cx);
    }

    // 右侧细滚动条（结果多于可视行时出现）
    void DrawScrollbar(IntPtr hdc, int cx)
    {
        if (_results.Length <= VisibleRows) return;
        int trackW = Scale(4);
        int x = cx - _pad - Scale(8);
        int top = _listTop + Scale(4);
        int h = _rowH * VisibleRows - Scale(8);
        var track = new Win32.RECT { L = x, T = top, R = x + trackW, B = top + h };
        Theme.FillRoundSolid(hdc, track, trackW, _brushTrack);

        int total = _results.Length;
        int thumbH = Math.Max(Scale(28), h * VisibleRows / total);
        int maxOffset = Math.Max(1, total - VisibleRows);
        int thumbY = top + (h - thumbH) * Math.Clamp(_offset, 0, maxOffset) / maxOffset;
        var thumb = new Win32.RECT { L = x, T = thumbY, R = x + trackW, B = thumbY + thumbH };
        Theme.FillRoundSolid(hdc, thumb, trackW, _brushThumb);
    }

    // 底部提示栏：快捷键说明 + 当前热键
    void DrawFooter(IntPtr hdc, int cx)
    {
        Theme.HLine(hdc, _pad + Scale(4), cx - _pad - Scale(4), _footerY, Theme.FooterLine);

        int capH = Scale(19);
        int capPad = Scale(6);
        int gap = Scale(6);
        int ty = _footerY + (_footerH - capH) / 2;
        int x = _pad + Scale(2);

        void Hint(string key, string label)
        {
            if (key.Length > 0)
            {
                x = Theme.DrawKeyCap(hdc, x, ty, capH, key, _fontKey, _brushCap, Theme.KeyCapText, capPad);
                x += gap;
            }
            Win32.SelectObject(hdc, _fontSmall);
            Win32.SetTextColor(hdc, (int)Theme.TextDim);
            var r = new Win32.RECT { L = x, T = _footerY, R = x + Scale(140), B = _footerY + _footerH };
            Win32.DrawTextW(hdc, label, -1, ref r, Theme.AlignLeft);
            x += Theme.MeasureText(hdc, _fontSmall, label) + Scale(14);
        }

        Hint("↑↓", "选择");
        Hint("Enter", "打开");
        Hint("Esc", "关闭");

        // 右侧：当前热键
        string hk = Program.Cfg.Hotkey;
        var rRight = new Win32.RECT { L = 0, T = _footerY, R = cx - _pad - Scale(2), B = _footerY + _footerH };
        Win32.SelectObject(hdc, _fontSmall);
        Win32.SetTextColor(hdc, (int)Theme.TextDim);
        Win32.DrawTextW(hdc, "呼出", -1, ref rRight, Theme.AlignRight);
        int hkW = Theme.MeasureText(hdc, _fontKey, hk) + capPad * 2;
        int hkX = rRight.R - Scale(32) - hkW;
        if (hkX > x)
            Theme.DrawKeyCap(hdc, hkX, ty, capH, hk, _fontKey, _brushCap, Theme.KeyCapText, capPad);
    }

    static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        int start = s.Length - (max - 2);
        if (s.IndexOf('\\', 0) >= 0)
        {
            // 路径：从后面往前保留完整目录段，避免把词切断（如 “…\nsys 2022 R2” ）
            int sep = s.IndexOf('\\', start);
            if (sep > 0 && sep < s.Length - 1) return "…" + s.Substring(sep);
            return "…\\" + s.Substring(start).TrimStart('\\');
        }
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
