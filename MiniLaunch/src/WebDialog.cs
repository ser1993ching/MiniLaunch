using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 网站快捷入口编辑窗：添加 / 编辑一个常用网址（名称 + 网址 + 关键词）
// 与 NameDialog 同一套风格：自绘窗口 + 原生 EDIT 子控件（IME 友好）+ 自绘按钮。
// 顶部有一排「常用站点」快捷标签（百度 / 淘宝 / GitHub …），点一下自动填好名称与网址，
// 但最终仍以用户自己填的内容为准（标签只是省去输入）。
public sealed class WebDialog
{
    const string ClassName = "MiniLaunchWebDlg";
    const int EditName = 1, EditUrl = 2, EditKw = 3;

    /// <summary>按钮索引。</summary>
    public const int BtnCancel = 0;
    public const int BtnOk = 1;

    /// <summary>预设标签的命中编号基址（>=100，避免与按钮编号冲突）。</summary>
    public const int ChipHitBase = 100;

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static readonly Win32.SubclassProcDelegate s_editProc = EditProcStatic;
    static WebDialog? s_this;

    /// <summary>是否已打开（避免重复弹窗）。</summary>
    public static bool IsOpen => s_this != null;

    public IntPtr Hwnd;
    IntPtr _editName, _editUrl, _editKw;

    readonly string _title;
    /// <summary>确认回调：(名称, 网址, 关键词) → true 表示接受并关闭。</summary>
    readonly Func<string, string, string, bool> _onOk;

    float _scale = 1f;
    int _w, _h, _pad, _editH;
    IntPtr _fontTitle, _fontHint, _fontEdit, _fontSmall, _fontBtn, _brushEditBg, _brushBorder;
    string _error = "";

    int _hover = -1;      // 鼠标悬停的按钮（-2 起为预设标签）
    int _pressed = -1;

    WebDialog(string title, Func<string, string, string, bool> onOk)
    {
        _title = title;
        _onOk = onOk;
    }

    /// <summary>弹出网站编辑框。initial* 为初始内容；onOk 返回 true 才关闭。</summary>
    public static void Show(string title, string name, string url, string keywords,
        Func<string, string, string, bool> onOk)
    {
        if (s_this != null) { Win32.SetForegroundWindow(s_this.Hwnd); return; }

        var self = new WebDialog(title, onOk);
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
        Win32.RegisterClassW(ref wc);

        IntPtr hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW,
            ClassName, title, Win32.WS_POPUP,
            0, 0, self._w, self._h,
            IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            s_this = null;
            return;
        }

        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        // 置顶：本程序的主窗口也是 TOPMOST，不显式抬一次会被主窗口盖住（点不到）
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
        Win32.SetForegroundWindow(hwnd);
        Win32.SetWindowTextW(self._editName, name ?? "");
        Win32.SetWindowTextW(self._editUrl, url ?? "");
        Win32.SetWindowTextW(self._editKw, keywords ?? "");
        // 光标默认落在「网址」上：名称可以留空（自动取域名）
        Win32.SetFocus(self._editUrl);
        Win32.SendMessageW(self._editUrl, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
    }

    // ================= 布局 =================
    void RecalcMetrics(float scale)
    {
        _scale = scale;
        _w = Scale(470);
        _h = Scale(340);
        _pad = Scale(22);
        _editH = Scale(30);
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    /// <summary>预设标签行（常用站点快捷填入）的高度与起点。</summary>
    public static int PresetTop(float scale) => (int)Math.Round(22 * scale) + (int)Math.Round(32 * scale);

    /// <summary>预设标签宽度：中文按 13px/字，其余按 7px/字估算（自检用）。</summary>
    public static int ChipWidth(string text, float scale)
    {
        int w = 0;
        foreach (char c in text) w += c > 0x2E80 ? 13 : 7;
        return (int)Math.Round((w + 20) * scale);
    }

    /// <summary>预设标签的 x 坐标序列（从 pad 起，间距 8px）。</summary>
    public static int[] PresetLayout(float scale)
    {
        int pad = (int)Math.Round(22 * scale);
        int gap = (int)Math.Round(8 * scale);
        var res = new List<int>();
        int x = pad;
        foreach (var p in WebUrl.Presets)
        {
            res.Add(x);
            x += ChipWidth(p.Name, scale) + gap;
        }
        return res.ToArray();
    }

    /// <summary>命中测试：点在某个预设标签上返回其序号，否则 -1。</summary>
    public static int HitPreset(int clientW, float scale, int mx, int my)
    {
        int top = PresetTop(scale);
        int h = (int)Math.Round(26 * scale);
        if (my < top || my >= top + h) return -1;
        var xs = PresetLayout(scale);
        for (int i = 0; i < xs.Length && i < WebUrl.Presets.Length; i++)
        {
            int w = ChipWidth(WebUrl.Presets[i].Name, scale);
            if (mx >= xs[i] && mx < xs[i] + w) return i;
        }
        return -1;
    }

    /// <summary>底部按钮布局（客户端坐标）：[x, y, w, h] × 2，顺序：取消、确定。</summary>
    public static int[] ButtonLayout(int clientW, int clientH, float scale)
    {
        int pad = (int)Math.Round(22 * scale);
        int btnH = (int)Math.Round(30 * scale);
        int okW = (int)Math.Round(88 * scale);
        int cancelW = (int)Math.Round(76 * scale);
        int gap = (int)Math.Round(10 * scale);
        int okX = clientW - pad - okW;
        int cancelX = okX - gap - cancelW;
        int y = clientH - pad - btnH;
        return new[] { cancelX, y, cancelW, btnH, okX, y, okW, btnH };
    }

    /// <summary>命中测试：点在「取消/确定」按钮上返回按钮索引，否则 -1。</summary>
    public static int HitButton(int clientW, int clientH, float scale, int mx, int my)
    {
        var r = ButtonLayout(clientW, clientH, scale);
        for (int i = 0; i < 2; i++)
        {
            int x = r[i * 4], y = r[i * 4 + 1], w = r[i * 4 + 2], h = r[i * 4 + 3];
            if (mx >= x && mx < x + w && my >= y && my < y + h) return i;
        }
        return -1;
    }

    /// <summary>统一命中测试：按钮优先，其次是预设标签；都没有则 -1。</summary>
    int HitAt(int mx, int my)
    {
        int hit = HitButton(_w, _h, _scale, mx, my);
        if (hit >= 0) return hit;
        int chip = HitPreset(_w, _scale, mx, my);
        return chip >= 0 ? ChipHitBase + chip : -1;
    }

    void CreateFonts()
    {
        _fontTitle = MakeFont(16, Win32.FW_SEMIBOLD);
        _fontHint = MakeFont(12, Win32.FW_NORMAL);
        _fontEdit = MakeFont(15, Win32.FW_NORMAL);
        _fontSmall = MakeFont(11, Win32.FW_NORMAL);
        _fontBtn = MakeFont(13, Win32.FW_NORMAL);
        _brushEditBg = Win32.CreateSolidBrush(Theme.Surface);
        _brushBorder = Win32.CreateSolidBrush(Theme.SurfaceBorder);
    }

    IntPtr MakeFont(int px, int weight) =>
        Win32.CreateFontW(-Scale(px), 0, 0, 0, weight, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");

    void DestroyRes()
    {
        foreach (var f in new[] { _fontTitle, _fontHint, _fontEdit, _fontSmall, _fontBtn })
            if (f != IntPtr.Zero) Win32.DeleteObject(f);
        _fontTitle = _fontHint = _fontEdit = _fontSmall = _fontBtn = IntPtr.Zero;
        if (_brushEditBg != IntPtr.Zero) { Win32.DeleteObject(_brushEditBg); _brushEditBg = IntPtr.Zero; }
        if (_brushBorder != IntPtr.Zero) { Win32.DeleteObject(_brushBorder); _brushBorder = IntPtr.Zero; }
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
            {
                Hwnd = h;
                RecalcMetrics(Dpi.ForWindow(h) / 96f);
                CreateFonts();

                IntPtr hinst = Win32.GetModuleHandleW(null);
                _editName = MakeEdit(h, EditName, "名称（留空则用域名）");
                _editUrl = MakeEdit(h, EditUrl, "网址，如 https://www.baidu.com");
                _editKw = MakeEdit(h, EditKw, "关键词（可选，用于搜索）");
                LayoutEdits();

                // 窗口按 96 DPI 创建：高 DPI 下撑到重算后的尺寸，并在光标所在显示器居中
                Win32.GetCursorPos(out var pt);
                IntPtr mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
                var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                int waL = 0, waT = 0, waW = 1920, waH = 1080;
                if (Win32.GetMonitorInfoW(mon, ref mi))
                {
                    waL = mi.rcMonitor.L; waT = mi.rcMonitor.T;
                    waW = mi.rcMonitor.Width; waH = mi.rcMonitor.Height;
                }
                Win32.SetWindowPos(h, IntPtr.Zero,
                    waL + (waW - _w) / 2, waT + (waH - _h) / 2, _w, _h,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                Theme.ApplyModernFrame(h);
                return IntPtr.Zero;
            }

            case Win32.WM_DPICHANGED:
            {
                RecalcMetrics((ushort)Win32.LOWORD(w) / 96f);
                DestroyRes();
                CreateFonts();
                LayoutEdits();
                var rect = Marshal.PtrToStructure<Win32.RECT>(l);
                Win32.SetWindowPos(h, IntPtr.Zero, rect.L, rect.T, rect.Width, rect.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                Win32.InvalidateRect(h, IntPtr.Zero, true);
                return IntPtr.Zero;
            }

            case Win32.WM_ERASEBKGND:
                return (IntPtr)1;

            case Win32.WM_PAINT:
                OnPaint();
                return IntPtr.Zero;

            case Win32.WM_CTLCOLOREDIT:
                // wParam 是 HDC，lParam 才是控件句柄（写反会让 EDIT 变白底）
                if (l == _editName || l == _editUrl || l == _editKw)
                {
                    Win32.SetTextColor(w, (int)Theme.TextMain);
                    Win32.SetBkColor(w, (int)Theme.Surface);
                    return _brushEditBg;
                }
                break;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
                // 只拦截 Enter/Esc，其余（含 Alt+F4 关窗手势）必须落到 DefWindowProc
                if (((int)w & 0xFF) == Win32.VK_ESCAPE) { Close(); return IntPtr.Zero; }
                if (((int)w & 0xFF) == Win32.VK_RETURN) { Commit(); return IntPtr.Zero; }
                break;

            case Win32.WM_COMMAND:
                // 用户开始输入后清掉上一次的错误提示
                if (Win32.HIWORD(w) == Win32.EN_CHANGE && _error.Length > 0 &&
                    (Win32.LOWORD(w) == EditName || Win32.LOWORD(w) == EditUrl || Win32.LOWORD(w) == EditKw))
                {
                    _error = "";
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            // ===== 鼠标：悬停 / 按下 / 点击 =====
            case Win32.WM_MOUSEMOVE:
            {
                int x = Win32.LOWORD(l), y = Win32.HIWORD(l);
                int hit = HitAt(x, y);
                if (hit != _hover)
                {
                    _hover = hit;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                var tme = new Win32.TRACKMOUSEEVENT
                {
                    cbSize = Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                    dwFlags = Win32.TME_LEAVE,
                    hwndTrack = h,
                    dwHoverTime = 0,
                };
                Win32.TrackMouseEvent(ref tme);
                return IntPtr.Zero;
            }

            case Win32.WM_MOUSELEAVE:
                if (_hover != -1 || _pressed != -1)
                {
                    _hover = -1;
                    _pressed = -1;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
            {
                int x = Win32.LOWORD(l), y = Win32.HIWORD(l);
                int hit = HitAt(x, y);
                if (hit >= 0)
                {
                    _pressed = hit;
                    _hover = hit;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_LBUTTONUP:
            {
                int x = Win32.LOWORD(l), y = Win32.HIWORD(l);
                int hit = HitAt(x, y);
                int pressed = _pressed;
                _pressed = -1;
                Win32.InvalidateRect(h, IntPtr.Zero, false);
                if (hit >= 0 && hit == pressed)
                {
                    if (hit == BtnOk) Commit();
                    else if (hit == BtnCancel) Close();
                    else if (hit >= ChipHitBase) ApplyPreset(hit - ChipHitBase);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_SETCURSOR:
                if (_hover != -1 && (int)Win32.LOWORD(l) == Win32.HTCLIENT)
                {
                    Win32.SetCursor(Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_HAND));
                    return (IntPtr)1;
                }
                break;

            case Win32.WM_DESTROY:
                DestroyRes();
                s_this = null;
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    IntPtr MakeEdit(IntPtr parent, int id, string cue)
    {
        IntPtr e = Win32.CreateWindowExW(0, "EDIT", "",
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.ES_LEFT | Win32.ES_AUTOHSCROLL,
            0, 0, 10, 10,
            parent, (IntPtr)id, Win32.GetModuleHandleW(null), IntPtr.Zero);
        Win32.SetWindowSubclass(e, s_editProc, (nuint)id, IntPtr.Zero);
        if (_fontEdit != IntPtr.Zero)
            Win32.SendMessageW(e, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        IntPtr c = Marshal.StringToHGlobalUni(cue);
        Win32.SendMessageW(e, Win32.EM_SETCUEBANNER, (IntPtr)1, c);
        Marshal.FreeHGlobal(c);
        return e;
    }

    void Close() => Win32.DestroyWindow(Hwnd);

    /// <summary>三个输入框的纵向位置（客户端坐标）。</summary>
    void LayoutEdits()
    {
        int labelY = _pad + Scale(74);
        int editY = labelY + Scale(18);
        PlaceEdit(_editName, editY);
        PlaceEdit(_editUrl, editY + Scale(58));
        PlaceEdit(_editKw, editY + Scale(116));
    }

    void PlaceEdit(IntPtr edit, int top)
    {
        if (edit == IntPtr.Zero) return;
        Win32.MoveWindow(edit, _pad, top, _w - _pad * 2, _editH, true);
        if (_fontEdit != IntPtr.Zero)
            Win32.SendMessageW(edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        int mg = Scale(10);
        Win32.SendMessageW(edit, Win32.EM_SETMARGINS,
            (IntPtr)(Win32.EC_LEFTMARGIN | Win32.EC_RIGHTMARGIN), (IntPtr)((mg << 16) | (mg & 0xFFFF)));
    }

    /// <summary>点击预设标签：把名称与网址填进去（不直接保存，仍需点「确定」）。</summary>
    void ApplyPreset(int index)
    {
        if (index < 0 || index >= WebUrl.Presets.Length) return;
        var p = WebUrl.Presets[index];
        Win32.SetWindowTextW(_editName, p.Name);
        Win32.SetWindowTextW(_editUrl, p.Url);
        Win32.SetFocus(_editUrl);
        Win32.SendMessageW(_editUrl, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
    }

    // ================= 输入 =================
    static string ReadText(IntPtr edit)
    {
        if (edit == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        Win32.GetWindowTextW(edit, sb, 512);
        return sb.ToString().Trim();
    }

    void Commit()
    {
        string url = ReadText(_editUrl);
        if (!WebUrl.IsValid(url))
        {
            _error = "网址无效：请填写 http:// 或 https:// 开头的地址";
            Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
            return;
        }
        string name = ReadText(_editName);
        if (name.Length == 0) name = WebUrl.DefaultName(url);   // 名称留空 → 自动取域名
        string kw = ReadText(_editKw);
        bool ok;
        try { ok = _onOk(name, url, kw); }
        catch { ok = false; }
        if (ok) { Close(); return; }
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
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
            Theme.GradientV(mem, all, Theme.BgTop, Theme.BgBottom);
            Win32.SetBkMode(mem, Win32.TRANSPARENT);

            // 标题
            Win32.SelectObject(mem, _fontTitle);
            Win32.SetTextColor(mem, (int)Theme.TextMain);
            var rTitle = new Win32.RECT { L = _pad, T = _pad, R = cx - _pad, B = _pad + Scale(24) };
            Win32.DrawTextW(mem, _title, -1, ref rTitle, Theme.AlignLeft);

            // 预设标签
            DrawPresets(mem, cx);

            // 三个字段的标签 + 输入框描边
            int labelY = _pad + Scale(74);
            int editY = labelY + Scale(18);
            DrawField(mem, cx, labelY, editY, "名称", _editName);
            DrawField(mem, cx, labelY + Scale(58), editY + Scale(58), "网址", _editUrl);
            DrawField(mem, cx, labelY + Scale(116), editY + Scale(116), "关键词（可选）", _editKw);

            // 错误提示
            if (_error.Length > 0)
            {
                Win32.SelectObject(mem, _fontSmall);
                Win32.SetTextColor(mem, (int)Theme.Warn);
                int ey = editY + Scale(116) + _editH + Scale(8);
                var rErr = new Win32.RECT { L = _pad, T = ey, R = cx - _pad, B = ey + Scale(16) };
                Win32.DrawTextW(mem, _error, -1, ref rErr, Theme.AlignLeft);
            }

            // 按钮
            var lay = ButtonLayout(cx, cy, _scale);
            DrawButton(mem, lay, BtnCancel, "取消", false);
            DrawButton(mem, lay, BtnOk, "确定", true);

            // 底部提示
            Win32.SelectObject(mem, _fontSmall);
            Win32.SetTextColor(mem, (int)Theme.TextDim);
            int by = lay[1];
            var rBot = new Win32.RECT { L = _pad, T = by, R = lay[0] - Scale(10), B = by + lay[3] };
            Win32.DrawTextW(mem, "Enter 确认  ·  Esc 取消", -1, ref rBot, Theme.AlignLeft);

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

    /// <summary>画一排「常用站点」快捷标签。</summary>
    void DrawPresets(IntPtr hdc, int cx)
    {
        int top = PresetTop(_scale);
        int h = Scale(26);
        var xs = PresetLayout(_scale);
        Win32.SelectObject(hdc, _fontSmall);
        for (int i = 0; i < WebUrl.Presets.Length && i < xs.Length; i++)
        {
            string name = WebUrl.Presets[i].Name;
            int w = ChipWidth(name, _scale);
            int x = xs[i];
            bool hover = _hover == ChipHitBase + i;
            bool pressed = _pressed == ChipHitBase + i;
            var rc = new Win32.RECT { L = x, T = top + (pressed ? 1 : 0), R = x + w, B = top + h + (pressed ? 1 : 0) };
            uint bg = pressed ? Win32.Rgb(0x2C, 0x3A, 0x66) : (hover ? Win32.Rgb(0x33, 0x3F, 0x6B) : Win32.Rgb(0x2C, 0x2F, 0x3A));
            IntPtr brBg = Win32.CreateSolidBrush(bg);
            Theme.FillRoundSolid(hdc, rc, Scale(7), brBg);
            Win32.DeleteObject(brBg);
            IntPtr brBorder = Win32.CreateSolidBrush(hover ? Theme.Accent : Theme.SurfaceBorder);
            var outer = new Win32.RECT { L = rc.L - 1, T = rc.T - 1, R = rc.R + 1, B = rc.B + 1 };
            Theme.FillRoundSolid(hdc, outer, Scale(7) + 1, brBorder);
            Win32.DeleteObject(brBorder);

            Win32.SetTextColor(hdc, (int)(hover ? Theme.TextMain : Theme.TextSub));
            var tr = new Win32.RECT { L = rc.L, T = rc.T, R = rc.R, B = rc.B };
            Win32.DrawTextW(hdc, name, -1, ref tr, Theme.AlignCenter);
        }
    }

    /// <summary>画一个字段：上方小标签 + 输入框描边。</summary>
    void DrawField(IntPtr hdc, int cx, int labelY, int editY, string label, IntPtr edit)
    {
        Win32.SelectObject(hdc, _fontHint);
        Win32.SetTextColor(hdc, (int)Theme.TextSub);
        var rLabel = new Win32.RECT { L = _pad, T = labelY, R = cx - _pad, B = labelY + Scale(18) };
        Win32.DrawTextW(hdc, label, -1, ref rLabel, Theme.AlignLeft);

        var box = new Win32.RECT
        {
            L = _pad - 1,
            T = editY - 1,
            R = cx - _pad + 1,
            B = editY + _editH + 1,
        };
        Theme.FillRoundSolid(hdc, box, Scale(8), _brushBorder);
    }

    /// <summary>画一个自绘按钮（主按钮用渐变，次按钮用描边）。</summary>
    void DrawButton(IntPtr hdc, int[] lay, int index, string text, bool primary)
    {
        int x = lay[index * 4], y = lay[index * 4 + 1], w = lay[index * 4 + 2], h = lay[index * 4 + 3];
        bool hover = _hover == index;
        bool pressed = _pressed == index;
        int dy = pressed ? 1 : 0;

        var rc = new Win32.RECT { L = x, T = y + dy, R = x + w, B = y + h + dy };
        if (primary)
        {
            uint top = pressed ? Theme.SelBottom : (hover ? Win32.Rgb(0x6E, 0x8C, 0xFF) : Theme.SelTop);
            uint bot = pressed ? Win32.Rgb(0x2F, 0x4A, 0xB8) : (hover ? Win32.Rgb(0x4E, 0x6B, 0xE8) : Theme.SelBottom);
            Theme.FillRoundGradient(hdc, rc, Scale(8), top, bot);
        }
        else
        {
            uint bg = pressed ? Win32.Rgb(0x22, 0x24, 0x2C) : (hover ? Theme.Hover : Theme.Surface);
            IntPtr brBorder = Win32.CreateSolidBrush(Theme.SurfaceBorder);
            var outer = new Win32.RECT { L = rc.L - 1, T = rc.T - 1, R = rc.R + 1, B = rc.B + 1 };
            Theme.FillRoundSolid(hdc, outer, Scale(8) + 1, brBorder);
            Win32.DeleteObject(brBorder);
            IntPtr brBg = Win32.CreateSolidBrush(bg);
            Theme.FillRoundSolid(hdc, rc, Scale(8), brBg);
            Win32.DeleteObject(brBg);
        }

        Win32.SelectObject(hdc, _fontBtn);
        Win32.SetTextColor(hdc, (int)(primary ? Theme.TextOnSel : Theme.TextMain));
        var tr = new Win32.RECT { L = rc.L, T = rc.T, R = rc.R, B = rc.B };
        Win32.DrawTextW(hdc, text, -1, ref tr, Theme.AlignCenter);
    }

    // ================= EDIT 子类化 =================
    static IntPtr EditProcStatic(IntPtr h, uint msg, IntPtr w, IntPtr l, IntPtr id, IntPtr data)
    {
        var self = s_this;
        if (self == null) return Win32.DefSubclassProc(h, msg, w, l);
        if (msg == Win32.WM_KEYDOWN)
        {
            int vk = (int)w & 0xFF;
            if (vk == Win32.VK_RETURN) { self.Commit(); return IntPtr.Zero; }
            if (vk == Win32.VK_ESCAPE) { self.Close(); return IntPtr.Zero; }
            if (vk == Win32.VK_TAB) { self.FocusNext(h); return IntPtr.Zero; }
        }
        // Enter / Esc / Tab 的 WM_CHAR 一并吞掉，否则 EDIT 会 MessageBeep「叮」一声（坑 #27）
        if (msg == Win32.WM_CHAR)
        {
            int ch = (int)w;
            if (ch == 0x0D || ch == 0x0A || ch == Win32.VK_ESCAPE || ch == 0x09) return IntPtr.Zero;
        }
        return Win32.DefSubclassProc(h, msg, w, l);
    }

    /// <summary>Tab 在三个输入框之间轮转（普通窗口没有对话框管理器的默认焦点切换）。</summary>
    void FocusNext(IntPtr cur)
    {
        IntPtr next = cur == _editName ? _editUrl : (cur == _editUrl ? _editKw : _editName);
        if (next != IntPtr.Zero)
        {
            Win32.SetFocus(next);
            Win32.SendMessageW(next, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
        }
    }
}
