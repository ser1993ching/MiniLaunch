using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 网站快捷入口编辑窗：添加 / 编辑一个常用网址（名称 + 网址）
// 与 NameDialog 同一套风格：自绘窗口 + 原生 EDIT 子控件（IME 友好）+ 自绘按钮。
// 底部是「已添加的网站」标签行：点标签 = 把该网站填进表单继续编辑，点标签上的 × = 删除该网站。
public sealed class WebDialog
{
    const string ClassName = "MiniLaunchWebDlg";
    const int EditName = 1, EditUrl = 2;

    /// <summary>按钮索引。</summary>
    public const int BtnCancel = 0;
    public const int BtnOk = 1;

    /// <summary>标签行的命中编号基址（>=100，避免与按钮编号冲突）。</summary>
    public const int TagHitBase = 100;

    /// <summary>标签上「×」删除区的命中编号基址（>=200）。</summary>
    public const int TagDelBase = 200;

    /// <summary>已添加网站标签（用于绘制与命中测试）。</summary>
    public sealed class TagItem
    {
        public string Name = "";
        public string Url = "";
        public int X, W;        // 客户坐标
        public int Row;
        public bool IsCurrent;  // 正在编辑的那一个
        public int DelW;        // 「×」区宽度
    }

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static readonly Win32.SubclassProcDelegate s_editProc = EditProcStatic;
    static WebDialog? s_this;

    /// <summary>是否已打开（避免重复弹窗）。</summary>
    public static bool IsOpen => s_this != null;

    public IntPtr Hwnd;
    IntPtr _editName, _editUrl;

    string _title;
    /// <summary>确认回调：(名称, 网址, 目标网址) → true 表示接受并关闭。目标网址为空表示新增。</summary>
    readonly Func<string, string, string, bool> _onOk;
    /// <summary>取当前已添加的网站列表（绘制与命中测试时实时取，删除后自动刷新）。</summary>
    readonly Func<List<WebSiteEntry>> _listProvider;
    /// <summary>删除某个网站（按网址）。</summary>
    readonly Action<string> _onDelete;

    /// <summary>当前正在编辑的网站网址（空 = 新增）。</summary>
    string _currentUrl = "";

    float _scale = 1f;
    int _w, _h, _pad, _boxH, _editH;
    IntPtr _fontTitle, _fontHint, _fontEdit, _fontSmall, _fontBtn;
    IntPtr _brushEditBg, _brushBorder, _brushSurface;
    string _error = "";

    int _hover = -1;      // 鼠标悬停的目标（按钮 / 标签 / 删除区）
    int _pressed = -1;

    WebDialog(string title, Func<List<WebSiteEntry>> listProvider,
        Func<string, string, string, bool> onOk, Action<string> onDelete)
    {
        _title = title;
        _listProvider = listProvider;
        _onOk = onOk;
        _onDelete = onDelete;
    }

    /// <summary>弹出网站编辑框。initial* 为初始内容；onOk 返回 true 才关闭。</summary>
    public static void Show(string title, string name, string url,
        Func<List<WebSiteEntry>> listProvider,
        Func<string, string, string, bool> onOk,
        Action<string> onDelete)
    {
        if (s_this != null) { Win32.SetForegroundWindow(s_this.Hwnd); return; }

        var self = new WebDialog(title, listProvider, onOk, onDelete);
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
        // 有网址说明是「编辑」已有条目
        self._currentUrl = string.IsNullOrEmpty(url) ? "" : url;
        Win32.SetFocus(self._editName);
        Win32.SendMessageW(self._editName, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
    }

    // ================= 布局 =================
    void RecalcMetrics(float scale)
    {
        _scale = scale;
        _w = Scale(470);
        _h = Scale(340);
        _pad = Scale(22);
        _boxH = Scale(30);   // 输入框（描边圆角）高度
        _editH = Scale(21);  // 真正的 EDIT 控件高度：单行 EDIT 的文字贴客户区顶部，控件本身要矮一点才居中
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    // 各区块的 y 坐标（客户坐标，96 DPI 基准）
    int LabelY1 => _pad + Scale(38);      // 「名称」标签
    int BoxY1 => LabelY1 + Scale(18);     // 名称输入框
    int LabelY2 => BoxY1 + _boxH + Scale(10);
    int BoxY2 => LabelY2 + Scale(18);     // 网址输入框
    int TagLabelY => BoxY2 + _boxH + Scale(10);
    int TagRow0Y => TagLabelY + Scale(20);
    int TagRowH => Scale(26);
    int TagRowGap => Scale(6);
    int ErrorY => TagRow0Y + (TagRowH + TagRowGap) * 2 + Scale(2);

    /// <summary>标签最多显示几行。</summary>
    public const int TagMaxRows = 2;

    /// <summary>文字宽度估算（中文 12px/字，其余 7px/字）：绘制与命中测试共用同一套算法。</summary>
    public static int ApproxWidth(string text, float scale)
    {
        int w = 0;
        foreach (char c in text) w += c > 0x2E80 ? 12 : 7;
        return (int)Math.Round(w * scale);
    }

    /// <summary>按当前网站列表生成标签布局（绘制与命中测试共用）。</summary>
    public static List<TagItem> BuildTags(IReadOnlyList<WebSiteEntry> sites, string currentUrl,
        int clientW, float scale)
    {
        var res = new List<TagItem>();
        if (sites == null) return res;
        int pad = (int)Math.Round(22 * scale);
        int gap = (int)Math.Round(6 * scale);
        int delW = (int)Math.Round(20 * scale);
        int x = pad;
        int row = 0;
        foreach (var s in sites)
        {
            string nm = string.IsNullOrWhiteSpace(s.Name) ? WebUrl.DefaultName(s.Url) : s.Name.Trim();
            if (nm.Length == 0) continue;
            int w = ApproxWidth(nm, scale) + delW + (int)Math.Round(14 * scale);
            if (x + w > clientW - pad)
            {
                row++;
                x = pad;
                if (row >= TagMaxRows) break;   // 放不下就不再显示（不滚动，避免复杂交互）
            }
            bool cur = !string.IsNullOrEmpty(currentUrl) &&
                       WebUrl.Normalize(s.Url).Equals(WebUrl.Normalize(currentUrl), StringComparison.OrdinalIgnoreCase);
            res.Add(new TagItem
            {
                Name = nm,
                Url = s.Url ?? "",
                X = x,
                W = w,
                Row = row,
                IsCurrent = cur,
                DelW = delW,
            });
            x += w + gap;
        }
        return res;
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

    void CreateFonts()
    {
        _fontTitle = MakeFont(16, Win32.FW_SEMIBOLD);
        _fontHint = MakeFont(12, Win32.FW_NORMAL);
        _fontEdit = MakeFont(15, Win32.FW_NORMAL);
        _fontSmall = MakeFont(11, Win32.FW_NORMAL);
        _fontBtn = MakeFont(13, Win32.FW_NORMAL);
        _brushEditBg = Win32.CreateSolidBrush(Theme.Surface);
        _brushBorder = Win32.CreateSolidBrush(Theme.SurfaceBorder);
        _brushSurface = Win32.CreateSolidBrush(Theme.Surface);
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
        foreach (var b in new IntPtr[] { _brushEditBg, _brushBorder, _brushSurface })
            if (b != IntPtr.Zero) Win32.DeleteObject(b);
        _brushEditBg = _brushBorder = _brushSurface = IntPtr.Zero;
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
                if (l == _editName || l == _editUrl)
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
                    (Win32.LOWORD(w) == EditName || Win32.LOWORD(w) == EditUrl))
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
                    else if (hit >= TagDelBase) DeleteTag(hit - TagDelBase);
                    else if (hit >= TagHitBase) PickTag(hit - TagHitBase);
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

    /// <summary>
    /// 两个输入框的摆放。单行 EDIT 的文字永远贴客户区顶部（EM_SETRECT 只对多行有效），
    /// 要让文字在框里视觉居中，只能把控件本身做矮（行高）并往下挪一点（见坑 #16）。
    /// </summary>
    void LayoutEdits()
    {
        PlaceEdit(_editName, BoxY1);
        PlaceEdit(_editUrl, BoxY2);
    }

    void PlaceEdit(IntPtr edit, int boxY)
    {
        if (edit == IntPtr.Zero) return;
        // 控件高 = 行高，顶部比框顶低 3px：文字（控件顶部往下约 3px 起）正好落在框的垂直中线
        int top = boxY + Scale(3);
        Win32.MoveWindow(edit, _pad, top, _w - _pad * 2, _editH, true);
        if (_fontEdit != IntPtr.Zero)
            Win32.SendMessageW(edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        int mg = Scale(10);
        Win32.SendMessageW(edit, Win32.EM_SETMARGINS,
            (IntPtr)(Win32.EC_LEFTMARGIN | Win32.EC_RIGHTMARGIN), (IntPtr)((mg << 16) | (mg & 0xFFFF)));
    }

    /// <summary>取当前已添加的网站列表。</summary>
    List<WebSiteEntry> CurrentSites()
    {
        try { return _listProvider() ?? new List<WebSiteEntry>(); }
        catch { return new List<WebSiteEntry>(); }
    }

    /// <summary>统一命中测试：按钮优先，其次是标签；都没有则 -1。</summary>
    int HitAt(int mx, int my)
    {
        int hit = HitButton(_w, _h, _scale, mx, my);
        if (hit >= 0) return hit;
        var tags = BuildTags(CurrentSites(), _currentUrl, _w, _scale);
        int rowH = TagRowH + TagRowGap;
        for (int i = 0; i < tags.Count; i++)
        {
            int y0 = TagRow0Y + tags[i].Row * rowH;
            if (my < y0 || my >= y0 + TagRowH) continue;
            if (mx < tags[i].X || mx >= tags[i].X + tags[i].W) continue;
            return (mx >= tags[i].X + tags[i].W - tags[i].DelW) ? TagDelBase + i : TagHitBase + i;
        }
        return -1;
    }

    /// <summary>点标签：把该网站填进表单（继续编辑它）。</summary>
    void PickTag(int index)
    {
        var tags = BuildTags(CurrentSites(), _currentUrl, _w, _scale);
        if (index < 0 || index >= tags.Count) return;
        var t = tags[index];
        string name = t.Name;
        string url = t.Url;
        foreach (var s in CurrentSites())
        {
            if (!WebUrl.Normalize(s.Url).Equals(WebUrl.Normalize(url), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(s.Name)) name = s.Name;
            break;
        }
        _currentUrl = url;
        _title = "编辑网站";
        Win32.SetWindowTextW(Hwnd, _title);
        Win32.SetWindowTextW(_editName, name);
        Win32.SetWindowTextW(_editUrl, url);
        Win32.SetFocus(_editName);
        Win32.SendMessageW(_editName, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, true);
    }

    /// <summary>点标签上的「×」：删除该网站。</summary>
    void DeleteTag(int index)
    {
        var tags = BuildTags(CurrentSites(), _currentUrl, _w, _scale);
        if (index < 0 || index >= tags.Count) return;
        string url = tags[index].Url;
        try { _onDelete(url); } catch { }
        if (!string.IsNullOrEmpty(_currentUrl) &&
            WebUrl.Normalize(_currentUrl).Equals(WebUrl.Normalize(url), StringComparison.OrdinalIgnoreCase))
        {
            // 删掉的正是正在编辑的那一个：表单清空，回到「新增」状态
            _currentUrl = "";
            _title = "添加网站";
            Win32.SetWindowTextW(Hwnd, _title);
            Win32.SetWindowTextW(_editName, "");
            Win32.SetWindowTextW(_editUrl, "");
        }
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, true);
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
        bool ok;
        try { ok = _onOk(name, url, _currentUrl); }
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

            // 两个字段
            DrawField(mem, cx, LabelY1, BoxY1, "名称");
            DrawField(mem, cx, LabelY2, BoxY2, "网址");

            // 底部：已添加的网站（标签行）
            DrawTags(mem, cx);

            // 错误提示
            if (_error.Length > 0)
            {
                Win32.SelectObject(mem, _fontSmall);
                Win32.SetTextColor(mem, (int)Theme.Warn);
                var rErr = new Win32.RECT { L = _pad, T = ErrorY, R = cx - _pad, B = ErrorY + Scale(16) };
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

    /// <summary>画一排「已添加的网站」标签：点标签=编辑，点 ×=删除。</summary>
    void DrawTags(IntPtr hdc, int cx)
    {
        Win32.SelectObject(hdc, _fontSmall);
        Win32.SetTextColor(hdc, (int)Theme.TextDim);
        var rLabel = new Win32.RECT { L = _pad, T = TagLabelY, R = cx - _pad, B = TagLabelY + Scale(18) };
        Win32.DrawTextW(hdc, "已添加的网站（点标签编辑，点 × 删除）", -1, ref rLabel, Theme.AlignLeft);

        var tags = BuildTags(CurrentSites(), _currentUrl, cx, _scale);
        if (tags.Count == 0)
        {
            Win32.SelectObject(hdc, _fontHint);
            Win32.SetTextColor(hdc, (int)Theme.TextDim);
            var rNone = new Win32.RECT { L = _pad, T = TagRow0Y, R = cx - _pad, B = TagRow0Y + TagRowH };
            Win32.DrawTextW(hdc, "（还没有添加网站）", -1, ref rNone, Theme.AlignLeft);
            return;
        }

        int rowH = TagRowH + TagRowGap;
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int y = TagRow0Y + t.Row * rowH;
            bool hover = _hover == TagHitBase + i || _hover == TagDelBase + i;
            bool hoverDel = _hover == TagDelBase + i;
            bool pressed = _pressed == TagHitBase + i || _pressed == TagDelBase + i;

            uint bg = pressed ? Win32.Rgb(0x2C, 0x3A, 0x66) : (hover ? Win32.Rgb(0x33, 0x3F, 0x6B) : Win32.Rgb(0x2C, 0x2F, 0x3A));
            // 先画描边（外圈），再画底色（内圈）：反过来会把底色整个盖掉
            IntPtr brBorder = Win32.CreateSolidBrush(t.IsCurrent ? Theme.Accent : (hover ? Theme.Accent : Theme.SurfaceBorder));
            var rc = new Win32.RECT { L = t.X, T = y, R = t.X + t.W, B = y + TagRowH };
            var outer = new Win32.RECT { L = rc.L - 1, T = rc.T - 1, R = rc.R + 1, B = rc.B + 1 };
            Theme.FillRoundSolid(hdc, outer, Scale(7) + 1, brBorder);
            Win32.DeleteObject(brBorder);
            IntPtr brBg = Win32.CreateSolidBrush(bg);
            Theme.FillRoundSolid(hdc, rc, Scale(7), brBg);
            Win32.DeleteObject(brBg);

            // 名称
            Win32.SelectObject(hdc, _fontSmall);
            Win32.SetTextColor(hdc, (int)((hover || t.IsCurrent) ? Theme.TextMain : Theme.TextSub));
            var tr = new Win32.RECT { L = t.X + Scale(9), T = y, R = t.X + t.W - t.DelW, B = y + TagRowH };
            Win32.DrawTextW(hdc, t.Name, -1, ref tr, Theme.AlignLeft);

            // 删除 ×
            Win32.SelectObject(hdc, _fontSmall);
            Win32.SetTextColor(hdc, (int)(hoverDel ? Theme.Warn : Theme.TextDim));
            var dr = new Win32.RECT { L = t.X + t.W - t.DelW, T = y, R = t.X + t.W, B = y + TagRowH };
            Win32.DrawTextW(hdc, "×", -1, ref dr, Theme.AlignCenter);
        }
    }

    /// <summary>画一个字段：上方小标签 + 输入框（描边圆角 + 底色）。</summary>
    void DrawField(IntPtr hdc, int cx, int labelY, int boxY, string label)
    {
        Win32.SelectObject(hdc, _fontHint);
        Win32.SetTextColor(hdc, (int)Theme.TextSub);
        var rLabel = new Win32.RECT { L = _pad, T = labelY, R = cx - _pad, B = labelY + Scale(18) };
        Win32.DrawTextW(hdc, label, -1, ref rLabel, Theme.AlignLeft);

        // 外圈描边 + 内圈底色：EDIT 控件比框矮（文字要居中），中间的空隙得由底色补上
        var outer = new Win32.RECT { L = _pad - 1, T = boxY - 1, R = cx - _pad + 1, B = boxY + _boxH + 1 };
        Theme.FillRoundSolid(hdc, outer, Scale(8) + 1, _brushBorder);
        var inner = new Win32.RECT { L = _pad, T = boxY, R = cx - _pad, B = boxY + _boxH };
        Theme.FillRoundSolid(hdc, inner, Scale(8), _brushSurface);
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

    /// <summary>Tab 在两个输入框之间轮转（普通窗口没有对话框管理器的默认焦点切换）。</summary>
    void FocusNext(IntPtr cur)
    {
        IntPtr next = cur == _editName ? _editUrl : _editName;
        if (next != IntPtr.Zero)
        {
            Win32.SetFocus(next);
            Win32.SendMessageW(next, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
        }
    }
}
