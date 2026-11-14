using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 名称输入小窗：给「文件夹快捷入口」起个显示名（Enter 确认 / Esc 取消）
// 自绘窗口 + 原生 EDIT 子控件（IME 友好），与 HotkeyDialog 同一套风格。
public sealed class NameDialog
{
    const string ClassName = "MiniLaunchNameDlg";
    const int EditId = 1;

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static readonly Win32.SubclassProcDelegate s_editProc = EditProcStatic;
    static NameDialog? s_this;

    /// <summary>是否已打开（避免重复弹窗）。</summary>
    public static bool IsOpen => s_this != null;

    public IntPtr Hwnd;
    IntPtr _edit;

    readonly string _title;
    readonly string _hint;
    readonly Func<string, bool> _onOk;   // 返回 true 表示接受并关闭

    float _scale = 1f;
    int _w, _h, _pad, _editH;
    IntPtr _fontTitle, _fontHint, _fontEdit, _fontSmall, _brushEditBg, _brushBorder;
    string _error = "";

    NameDialog(string title, string hint, Func<string, bool> onOk)
    {
        _title = title;
        _hint = hint;
        _onOk = onOk;
    }

    /// <summary>弹出名称输入框。initial 为默认名称；onOk 返回 true 才关闭。</summary>
    public static void Show(string title, string hint, string initial, Func<string, bool> onOk)
    {
        if (s_this != null) { Win32.SetForegroundWindow(s_this.Hwnd); return; }

        var self = new NameDialog(title, hint, onOk);
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
        _h = Scale(206);
        _pad = Scale(22);
        _editH = Scale(30);
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    void CreateFonts()
    {
        _fontTitle = MakeFont(16, Win32.FW_SEMIBOLD);
        _fontHint = MakeFont(12, Win32.FW_NORMAL);
        _fontEdit = MakeFont(15, Win32.FW_NORMAL);
        _fontSmall = MakeFont(11, Win32.FW_NORMAL);
        _brushEditBg = Win32.CreateSolidBrush(Theme.Surface);
        _brushBorder = Win32.CreateSolidBrush(Theme.SurfaceBorder);
    }

    IntPtr MakeFont(int px, int weight) =>
        Win32.CreateFontW(-Scale(px), 0, 0, 0, weight, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");

    void DestroyRes()
    {
        foreach (var f in new[] { _fontTitle, _fontHint, _fontEdit, _fontSmall })
            if (f != IntPtr.Zero) Win32.DeleteObject(f);
        _fontTitle = _fontHint = _fontEdit = _fontSmall = IntPtr.Zero;
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
                if (((int)w & 0xFF) == Win32.VK_ESCAPE) { Close(); return IntPtr.Zero; }
                if (((int)w & 0xFF) == Win32.VK_RETURN) { Commit(); return IntPtr.Zero; }
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                DestroyRes();
                s_this = null;
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    void Close() => Win32.DestroyWindow(Hwnd);

    void LayoutEdit()
    {
        if (_edit == IntPtr.Zero) return;
        int t = _pad + Scale(56);
        Win32.MoveWindow(_edit, _pad, t, _w - _pad * 2, _editH, true);
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
        try { ok = _onOk(text); }
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

            // 说明（路径等）
            Win32.SelectObject(mem, _fontHint);
            Win32.SetTextColor(mem, (int)Theme.TextSub);
            int hy = _pad + Scale(28);
            var rHint = new Win32.RECT { L = _pad, T = hy, R = cx - _pad, B = hy + Scale(18) };
            Win32.DrawTextW(mem, _hint, -1, ref rHint, Theme.AlignLeft);

            // 输入框外框（EDIT 自己填底色，这里只画 1px 描边圆角）
            var box = new Win32.RECT
            {
                L = _pad - 1,
                T = _pad + Scale(55),
                R = cx - _pad + 1,
                B = _pad + Scale(56) + _editH + 1,
            };
            Theme.FillRoundSolid(mem, box, Scale(8), _brushBorder);

            // 错误提示
            if (_error.Length > 0)
            {
                Win32.SelectObject(mem, _fontSmall);
                Win32.SetTextColor(mem, (int)Theme.Warn);
                int ey = _pad + Scale(56) + _editH + Scale(10);
                var rErr = new Win32.RECT { L = _pad, T = ey, R = cx - _pad, B = ey + Scale(16) };
                Win32.DrawTextW(mem, _error, -1, ref rErr, Theme.AlignLeft);
            }

            // 底部提示
            Win32.SelectObject(mem, _fontSmall);
            Win32.SetTextColor(mem, (int)Theme.TextDim);
            int by = cy - _pad - Scale(16);
            var rBot = new Win32.RECT { L = _pad, T = by, R = cx - _pad, B = by + Scale(16) };
            Win32.DrawTextW(mem, "Enter 确认  ·  Esc 取消", -1, ref rBot, Theme.AlignCenter);

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
