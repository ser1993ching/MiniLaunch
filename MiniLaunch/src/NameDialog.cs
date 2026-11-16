using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 名称输入小窗：给「文件夹快捷入口」起名 / 改名（Enter 确认 / Esc 取消，也可点按钮）
// 自绘窗口 + 原生 EDIT 子控件（IME 友好），与 HotkeyDialog 同一套风格。
// 底部两个按钮（取消 / 确定）是**自绘**的：原生 BUTTON 控件在暗色主题下是灰色的方块，
// 与整体风格不搭；自绘还能自己控制圆角、悬停与按下反馈。
// 调用方传入文件夹列表时，底部会多一行「已添加的文件夹」标签（与网站窗同一套 TagRow）：
// 点标签=改这一条的显示名，点 ×=直接删掉它。
public sealed class NameDialog
{
    const string ClassName = "MiniLaunchNameDlg";
    const int EditId = 1;

    /// <summary>按钮索引。</summary>
    public const int BtnCancel = 0;
    public const int BtnOk = 1;

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static readonly Win32.SubclassProcDelegate s_editProc = EditProcStatic;
    static NameDialog? s_this;

    /// <summary>是否已打开（避免重复弹窗）。</summary>
    public static bool IsOpen => s_this != null;

    public IntPtr Hwnd;
    IntPtr _edit;

    // 标题与路径都可变：点底部标签等于「改成那一条」，标题会切成「重命名文件夹」
    string _title;
    string _hint;
    /// <summary>当前操作的目标路径：默认是刚选中的目录，点标签可换成已添加的其他文件夹。</summary>
    string _targetPath;
    /// <summary>确认回调：(名称, 目标路径) → true 表示接受并关闭。</summary>
    readonly Func<string, string, bool> _onOk;
    /// <summary>非空时底部显示「已添加的文件夹」标签行（取当前列表）。</summary>
    readonly Func<List<CustomEntry>>? _folderProvider;
    /// <summary>删除某个文件夹快捷入口（按路径）。</summary>
    readonly Action<string>? _onDeleteFolder;
    List<TagRow.Tag>? _tags;
    /// <summary>放不下而没画的条目数（绘制时提示「另有 N 个未显示」）。</summary>
    int _tagsHidden;

    float _scale = 1f;
    int _w, _h, _pad, _fieldH;
    IntPtr _fontTitle, _fontHint, _fontEdit, _fontSmall, _fontBtn, _brushEditBg;
    string _error = "";

    int _hover = -1;      // 鼠标悬停的按钮/标签（TagRow 的命中编号）
    int _pressed = -1;    // 按下未松开的按钮/标签

    NameDialog(string title, string hint, Func<string, string, bool> onOk,
        Func<List<CustomEntry>>? folderProvider, Action<string>? onDeleteFolder)
    {
        _title = title;
        _hint = hint;
        _targetPath = hint ?? "";
        _onOk = onOk;
        _folderProvider = folderProvider;
        _onDeleteFolder = onDeleteFolder;
    }

    /// <summary>
    /// 弹出名称输入框。initial 为默认名称；onOk(名称, 路径) 返回 true 才关闭。
    /// folders / onDelete 非空时，底部额外显示「已添加的文件夹」标签行（点标签改名、点 × 删除）。
    /// </summary>
    public static void Show(string title, string hint, string initial, Func<string, string, bool> onOk,
        Func<List<CustomEntry>>? folders = null, Action<string>? onDelete = null)
    {
        if (s_this != null) { Win32.SetForegroundWindow(s_this.Hwnd); return; }

        var self = new NameDialog(title, hint, onOk, folders, onDelete);
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
        Win32.SetWindowTextW(self._edit, initial ?? "");
        Win32.SetFocus(self._edit);
        // 全选默认名称：用户直接输入即可覆盖
        Win32.SendMessageW(self._edit, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
    }

    // ================= 布局 =================
    void RecalcMetrics(float scale)
    {
        _scale = scale;
        _w = Scale(460);
        _h = HeightFor(_folderProvider != null, scale);
        _pad = Scale(22);
        _fieldH = Scale(32);   // 输入框高度（含 1px 描边）
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    /// <summary>窗口高度：带文件夹标签行时更高（允许 4 行标签；自检会用它算按钮/标签是否重叠）。</summary>
    public static int HeightFor(bool withTags, float scale) => (int)Math.Round((withTags ? 352 : 214) * scale);

    // 标签行的纵向布局（行高/间距与网站窗一致；名称窗允许 4 行，网站窗 2 行）
    const int TagMaxRows = 4;
    int TagLabelY => Scale(118);
    int TagRow0Y => Scale(140);
    int TagRowH => Scale(26);
    int TagRowGap => Scale(6);
    /// <summary>错误提示的 y：标签行在时得让开 TagMaxRows 行标签。</summary>
    int ErrorY => _folderProvider != null
        ? TagRow0Y + (TagRowH + TagRowGap) * TagMaxRows + Scale(4)
        : _pad + Scale(54) + _fieldH + Scale(9);

    /// <summary>
    /// 输入框几何（外框 + 内部 EDIT）。两个窗共用 Theme.SingleLineField：
    /// EDIT 必须比框小一圈，否则它的方角会把圆角描边切出四个「缺角」；
    /// 控件高度取行高再居中，文字才不会贴在框顶。
    /// </summary>
    (Win32.RECT Box, Win32.RECT Edit) Field(int cx) =>
        Theme.SingleLineField(_pad - 1, cx - _pad + 1, _pad + Scale(54), _fieldH, _scale);

    /// <summary>
    /// 底部按钮布局（客户端坐标）。返回 [x, y, w, h] × 2：第 0 个是「取消」，第 1 个是「确定」。
    /// 抽成静态方法是为了让自检能在无头环境里验证命中区（点击确认全靠这套坐标）；
    /// 具体尺寸已统一到 Theme.DialogButtonLayout（与网站窗共用一份）。
    /// </summary>
    public static int[] ButtonLayout(int clientW, int clientH, float scale) =>
        Theme.DialogButtonLayout(clientW, clientH, scale);

    /// <summary>命中测试：点在「取消/确定」按钮上返回按钮索引，否则返回 -1。</summary>
    public static int HitButton(int clientW, int clientH, float scale, int mx, int my) =>
        Theme.HitDialogButton(clientW, clientH, scale, mx, my);

    void CreateFonts()
    {
        _fontTitle = Theme.MakeFont(Theme.UiFont, 16, Win32.FW_SEMIBOLD, _scale);
        _fontHint = Theme.MakeFont(Theme.UiFont, 12, Win32.FW_NORMAL, _scale);
        _fontEdit = Theme.MakeFont(Theme.UiFont, 15, Win32.FW_NORMAL, _scale);
        _fontSmall = Theme.MakeFont(Theme.UiFont, 11, Win32.FW_NORMAL, _scale);
        _fontBtn = Theme.MakeFont(Theme.UiFont, 13, Win32.FW_NORMAL, _scale);
        _brushEditBg = Win32.CreateSolidBrush(Theme.Surface);
    }

    void DestroyRes()
    {
        foreach (var f in new[] { _fontTitle, _fontHint, _fontEdit, _fontSmall, _fontBtn })
            if (f != IntPtr.Zero) Win32.DeleteObject(f);
        _fontTitle = _fontHint = _fontEdit = _fontSmall = _fontBtn = IntPtr.Zero;
        if (_brushEditBg != IntPtr.Zero) { Win32.DeleteObject(_brushEditBg); _brushEditBg = IntPtr.Zero; }
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

                // 输入框（原生 EDIT，IME 友好）
                _edit = Win32.CreateWindowExW(0, "EDIT", "",
                    Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.ES_LEFT | Win32.ES_AUTOHSCROLL,
                    0, 0, 10, 10,
                    h, (IntPtr)EditId, Win32.GetModuleHandleW(null), IntPtr.Zero);
                Win32.SetWindowSubclass(_edit, s_editProc, 1, IntPtr.Zero);
                LayoutEdit();

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
                LayoutEdit();
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
                if (l == _edit)
                {
                    Win32.SetTextColor(w, (int)Theme.TextMain);
                    Win32.SetBkColor(w, (int)Theme.Surface);
                    return _brushEditBg;
                }
                break;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
                // 只拦截 Enter/Esc，其余（含 Alt+F4 关窗手势）必须落到 DefWindowProc，
                // 否则 Alt+F4 被吞掉，窗口只能靠按钮/ESC 关闭。
                if (((int)w & 0xFF) == Win32.VK_ESCAPE) { Close(); return IntPtr.Zero; }
                if (((int)w & 0xFF) == Win32.VK_RETURN) { Commit(); return IntPtr.Zero; }
                break;

            case Win32.WM_COMMAND:
                // 用户开始输入后清掉上一次的错误提示（否则「名称不能为空」会一直挂在界面上）
                if (Win32.LOWORD(w) == EditId && Win32.HIWORD(w) == Win32.EN_CHANGE && _error.Length > 0)
                {
                    _error = "";
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            // ===== 鼠标：悬停 / 按下 / 点击（按钮 + 底部标签）=====
            case Win32.WM_MOUSEMOVE:
            {
                int x = Win32.LOWORD(l), y = Win32.HIWORD(l);
                int hit = HitAt(x, y);
                if (hit != _hover)
                {
                    _hover = hit;
                    Win32.InvalidateRect(h, IntPtr.Zero, false);
                }
                // 追踪离开事件：鼠标移出窗口时清掉悬停态
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
                    else if (hit >= TagRow.DelBase) DeleteTag(hit - TagRow.DelBase);
                    else if (hit >= TagRow.HitBase) PickTag(hit - TagRow.HitBase);
                }
                return IntPtr.Zero;
            }

            case Win32.WM_SETCURSOR:
                // 悬停在按钮上时显示手型光标
                if (_hover >= 0 && (int)Win32.LOWORD(l) == Win32.HTCLIENT)
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

    void Close() => Win32.DestroyWindow(Hwnd);

    /// <summary>统一命中测试：按钮优先，其次底部标签；都没有则 -1。</summary>
    int HitAt(int mx, int my)
    {
        int hit = HitButton(_w, _h, _scale, mx, my);
        if (hit >= 0) return hit;
        if (_folderProvider == null) return -1;
        return TagRow.Hit(Tags(), TagRow0Y, TagRowH, TagRowGap, mx, my);
    }

    // ================= 底部「已添加的文件夹」标签 =================

    /// <summary>当前标签布局（缓存：鼠标移动/重绘频率很高，不必每次重建）。</summary>
    List<TagRow.Tag> Tags()
    {
        if (_tags != null) return _tags;
        var items = new List<(string Name, string Key)>();
        try
        {
            var folders = _folderProvider?.Invoke();
            if (folders != null)
                foreach (var c in folders)
                {
                    string nm = string.IsNullOrWhiteSpace(c.Name) ? FolderNameOf(c.Path) : c.Name.Trim();
                    items.Add((nm, c.Path));
                }
        }
        catch { }
        _tags = TagRow.Build(items, _targetPath, _w, _scale, TagMaxRows, 22f, out _tagsHidden);
        return _tags;
    }

    /// <summary>目录默认显示名（取目录名；根目录/非法路径回退为原路径）。</summary>
    static string FolderNameOf(string path)
    {
        try
        {
            string n = new DirectoryInfo((path ?? "").TrimEnd('\\')).Name;
            return n.Length > 0 ? n : path ?? "";
        }
        catch { return path ?? ""; }
    }

    /// <summary>列表/当前目标/DPI 变化后失效（否则会点到旧位置）。</summary>
    void InvalidateTags() => _tags = null;

    /// <summary>点标签：把该文件夹填进表单（改成它的显示名）。</summary>
    void PickTag(int index)
    {
        var tags = Tags();
        if (index < 0 || index >= tags.Count) return;
        var t = tags[index];
        string name = t.Name;
        try
        {
            var folders = _folderProvider?.Invoke();
            if (folders != null)
                foreach (var c in folders)
                {
                    if (!c.Path.Equals(t.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(c.Name)) name = c.Name.Trim();
                    break;
                }
        }
        catch { }
        _targetPath = t.Key;
        _title = "重命名文件夹";
        _hint = t.Key;
        Win32.SetWindowTextW(Hwnd, _title);
        Win32.SetWindowTextW(_edit, name);
        Win32.SetFocus(_edit);
        Win32.SendMessageW(_edit, Win32.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
        InvalidateTags();   // 高亮项变了
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, true);
    }

    /// <summary>点标签上的「×」：删掉该文件夹快捷入口。</summary>
    void DeleteTag(int index)
    {
        var tags = Tags();
        if (index < 0 || index >= tags.Count) return;
        string path = tags[index].Key;
        try { _onDeleteFolder?.Invoke(path); } catch { }
        if (string.Equals(path, _targetPath, StringComparison.OrdinalIgnoreCase))
        {
            // 删掉的正是当前目标：标题回到「添加」（路径留着，确认即重新添加）
            _title = "添加文件夹";
            Win32.SetWindowTextW(Hwnd, _title);
        }
        InvalidateTags();   // 列表变了
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, true);
    }

    void LayoutEdit()
    {
        if (_edit == IntPtr.Zero) return;
        var (_, edit) = Field(_w);
        Win32.MoveWindow(_edit, edit.L, edit.T, edit.Width, edit.Height, true);
        if (_fontEdit != IntPtr.Zero)
            Win32.SendMessageW(_edit, Win32.WM_SETFONT, _fontEdit, (IntPtr)1);
        int mg = Scale(10);
        Win32.SendMessageW(_edit, Win32.EM_SETMARGINS,
            (IntPtr)(Win32.EC_LEFTMARGIN | Win32.EC_RIGHTMARGIN), (IntPtr)((mg << 16) | (mg & 0xFFFF)));
    }

    // ================= 输入 =================
    string ReadText()
    {
        var sb = new StringBuilder(256);
        Win32.GetWindowTextW(_edit, sb, 256);
        return sb.ToString().Trim();
    }

    void Commit()
    {
        string text = ReadText();
        if (text.Length == 0)
        {
            _error = "名称不能为空";
            Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
            return;
        }
        bool ok;
        try { ok = _onOk(text, _targetPath); }
        catch { ok = false; }
        if (ok) { Close(); return; }
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    // ================= 绘制 =================
    void OnPaint() => Theme.BufferedPaint(Hwnd, DrawContent);

    void DrawContent(IntPtr mem, int cx, int cy)
    {
        var all = new Win32.RECT { L = 0, T = 0, R = cx, B = cy };
        Theme.GradientV(mem, all, Theme.BgTop, Theme.BgBottom);
        Win32.SetBkMode(mem, Win32.TRANSPARENT);

        // 标题
        Win32.SelectObject(mem, _fontTitle);
        Win32.SetTextColor(mem, (int)Theme.TextMain);
        var rTitle = new Win32.RECT { L = _pad, T = _pad, R = cx - _pad, B = _pad + Scale(24) };
        Win32.DrawTextW(mem, _title, -1, ref rTitle, Theme.AlignLeft);

        // 说明（路径等）
        Win32.SelectObject(mem, _fontHint);
        Win32.SetTextColor(mem, (int)Theme.TextSub);
        int hy = _pad + Scale(28);
        var rHint = new Win32.RECT { L = _pad, T = hy, R = cx - _pad, B = hy + Scale(18) };
        Win32.DrawTextW(mem, _hint, -1, ref rHint, Theme.AlignLeft);

        // 输入框：1px 圆角描边 + 内圈底色（都由父窗口画），EDIT 内缩坐在里面（否则方角会切掉圆角）
        var (box, _) = Field(cx);
        Theme.RoundBorderBox(mem, box, Scale(9), Theme.Surface);

        // 底部：已添加的文件夹标签（与网站窗同一套 TagRow：点标签改名、点 × 删除）
        if (_folderProvider != null)
        {
            var tags = Tags();
            TagRow.Draw(mem, tags, TagLabelY, TagRow0Y, TagRowH, TagRowGap, _hover, _pressed,
                _fontSmall, _fontHint, cx, _scale,
                "已添加的文件夹（点标签改名，点 × 删除）", "（还没有添加文件夹）",
                hidden: _tagsHidden);
        }

        // 错误提示（有标签行时让开两行标签）
        if (_error.Length > 0)
        {
            Win32.SelectObject(mem, _fontSmall);
            Win32.SetTextColor(mem, (int)Theme.Warn);
            int ey = ErrorY;
            var rErr = new Win32.RECT { L = _pad, T = ey, R = cx - _pad, B = ey + Scale(16) };
            Win32.DrawTextW(mem, _error, -1, ref rErr, Theme.AlignLeft);
        }

        // 按钮
        var lay = ButtonLayout(cx, cy, _scale);
        DrawButton(mem, lay, BtnCancel, "取消", false);
        DrawButton(mem, lay, BtnOk, "确定", true);

        // 底部提示（左侧）
        Win32.SelectObject(mem, _fontSmall);
        Win32.SetTextColor(mem, (int)Theme.TextDim);
        int by = lay[1];
        var rBot = new Win32.RECT { L = _pad, T = by, R = lay[0] - Scale(10), B = by + lay[3] };
        Win32.DrawTextW(mem, "Enter 确认  ·  Esc 取消", -1, ref rBot, Theme.AlignLeft);
    }

    /// <summary>画一个自绘按钮（实现已统一到 Theme.DrawButton，与网站窗共用）。</summary>
    void DrawButton(IntPtr hdc, int[] lay, int index, string text, bool primary) =>
        Theme.DrawButton(hdc, lay, index, text, primary, _hover == index, _pressed == index, _fontBtn, _scale);

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
        }
        // Enter / Esc 的 WM_CHAR 一并吞掉，否则 EDIT 会 MessageBeep「叮」一声（坑 #27）
        if (msg == Win32.WM_CHAR)
        {
            int ch = (int)w;
            if (ch == 0x0D || ch == 0x0A || ch == Win32.VK_ESCAPE) return IntPtr.Zero;
        }
        return Win32.DefSubclassProc(h, msg, w, l);
    }
}
