using System.Runtime.InteropServices;

namespace MiniLaunch;

// 热键录制小窗：直接按下组合键即可生效，Esc 取消
public sealed class HotkeyDialog
{
    const string ClassName = "MiniLaunchHotkey";

    static readonly Win32.WndProcDelegate s_wndProc = WndProcStatic;
    static HotkeyDialog? s_this;
    public static bool IsOpen => s_this != null;
    public static IntPtr InstanceHwnd => s_this?.Hwnd ?? IntPtr.Zero;
    /// <summary>预览模式（调试/截图）：失焦不自动关闭。</summary>
    public static bool PreviewMode;

    public IntPtr Hwnd;
    readonly Func<string, bool> _apply;
    readonly Action _onClose;

    float _scale = 1f;
    int _w, _h, _pad, _capH, _capPadX, _capGap;
    IntPtr _fontTitle, _fontHint, _fontKey, _fontSmall, _bCap;

    bool _captured;
    uint _mods, _vk;
    string _error = "";

    HotkeyDialog(Func<string, bool> apply, Action onClose)
    {
        _apply = apply;
        _onClose = onClose;
    }

    public static void Show(Func<string, bool> apply, Action onClose)
    {
        if (s_this != null) { Win32.SetForegroundWindow(s_this.Hwnd); return; }

        var self = new HotkeyDialog(apply, onClose);
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

        // 定位与 DPI 尺寸统一在 WM_CREATE 里做（创建前还不知道目标显示器 DPI）
        IntPtr hwnd = Win32.CreateWindowExW(
            Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW,
            ClassName, "设置热键", Win32.WS_POPUP,
            0, 0, self._w, self._h,
            IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            s_this = null;
            try { onClose(); } catch { } // 创建失败也要走关闭回调：调用方已注销旧热键，必须重注册
            return;
        }

        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(hwnd);
        Win32.SetFocus(hwnd);
    }

    // ================= 布局 =================
    void RecalcMetrics(float scale)
    {
        _scale = scale;
        _w = Scale(440);
        _h = Scale(196);
        _pad = Scale(22);
        _capH = Scale(38);
        _capPadX = Scale(11);
        _capGap = Scale(8);
    }

    int Scale(int v) => (int)Math.Round(v * _scale);

    void CreateFonts()
    {
        _fontTitle = MakeFont(16, Win32.FW_SEMIBOLD);
        _fontHint = MakeFont(13, Win32.FW_NORMAL);
        _fontKey = MakeFont(15, Win32.FW_SEMIBOLD);
        _fontSmall = MakeFont(12, Win32.FW_NORMAL);
        _bCap = Win32.CreateSolidBrush(Theme.KeyCapBg);
    }

    IntPtr MakeFont(int px, int weight) =>
        Win32.CreateFontW(-Scale(px), 0, 0, 0, weight, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, "Microsoft YaHei UI");

    void DestroyRes()
    {
        foreach (var f in new[] { _fontTitle, _fontHint, _fontKey, _fontSmall })
            if (f != IntPtr.Zero) Win32.DeleteObject(f);
        _fontTitle = _fontHint = _fontKey = _fontSmall = IntPtr.Zero;
        if (_bCap != IntPtr.Zero) { Win32.DeleteObject(_bCap); _bCap = IntPtr.Zero; }
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
                // 窗口按 96 DPI 尺寸创建：高 DPI 下撑到重算后的尺寸，并在光标所在显示器重新居中
                Win32.GetCursorPos(out var pt);
                IntPtr mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
                var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                int waL = 0, waT = 0, waW = 1920, waH = 1080;
                if (Win32.GetMonitorInfoW(mon, ref mi))
                {
                    waL = mi.rcWork.L; waT = mi.rcWork.T;
                    waW = mi.rcWork.Width; waH = mi.rcWork.Height;
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

            case Win32.WM_ACTIVATE:
                if (Win32.LOWORD(w) == 0 && !PreviewMode)
                {
                    uint fgThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
                    if (fgThread != Win32.GetCurrentThreadId()) Close();
                }
                return IntPtr.Zero;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
                OnKey((int)w & 0xFF);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
                Win32.SetFocus(h);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                DestroyRes();
                s_this = null;
                try { _onClose(); } catch { }
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    void Close() => Win32.DestroyWindow(Hwnd);

    // ================= 键盘录制 =================
    void OnKey(int vk)
    {
        if (vk == Win32.VK_ESCAPE) { Close(); return; }
        if (HotkeyParser.IsModifierKey(vk)) return;      // 只按修饰键不算
        if (vk == Win32.VK_RETURN) { if (_captured) Commit(); return; }

        uint mods = HotkeyParser.ModsFromKeyboard();
        if (mods == 0)
        {
            _error = "需要同时按住 Ctrl / Alt / Shift / Win 中至少一个";
            _captured = false;
            Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
            return;
        }
        _mods = mods;
        _vk = (uint)vk;
        _captured = true;
        _error = "";
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
        Commit();
    }

    void Commit()
    {
        string text = HotkeyParser.Format(_mods, _vk);
        if (_apply(text)) { Close(); return; }
        _error = $"“{text}”已被占用，换一个组合";
        _captured = false;
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
            Win32.DrawTextW(mem, "设置热键", -1, ref rTitle, Theme.AlignLeft);

            // 副标题
            Win32.SelectObject(mem, _fontHint);
            Win32.SetTextColor(mem, (int)Theme.TextSub);
            int hy = _pad + Scale(28);
            var rHint = new Win32.RECT { L = _pad, T = hy, R = cx - _pad, B = hy + Scale(20) };
            Win32.DrawTextW(mem, _captured ? "按 Enter 使用当前组合" : "直接按下想要的组合键", -1, ref rHint, Theme.AlignLeft);

            // 组合键：按键帽居中排列，帽间用“+”连接
            string[] caps = _captured
                ? HotkeyParser.Format(_mods, _vk).Split('+')
                : Program.Cfg.Hotkey.Split('+');
            int plusW = Theme.MeasureText(mem, _fontHint, "+");
            int totalW = 0;
            foreach (var c in caps) totalW += Theme.MeasureText(mem, _fontKey, c) + _capPadX * 2;
            if (caps.Length > 1) totalW += (caps.Length - 1) * (plusW + _capGap * 2);
            int x = (cx - totalW) / 2;
            int cy2 = _pad + Scale(58);
            for (int i = 0; i < caps.Length; i++)
            {
                x = Theme.DrawKeyCap(mem, x, cy2, _capH, caps[i], _fontKey, _bCap, Theme.KeyCapText, _capPadX);
                if (i < caps.Length - 1)
                {
                    Win32.SelectObject(mem, _fontHint);
                    Win32.SetTextColor(mem, (int)Theme.TextSub);
                    var rPlus = new Win32.RECT { L = x + _capGap, T = cy2, R = x + _capGap + plusW, B = cy2 + _capH };
                    Win32.DrawTextW(mem, "+", -1, ref rPlus, Theme.AlignCenter);
                    x += plusW + _capGap * 2;
                }
            }

            // 错误提示 / 底部说明
            Win32.SelectObject(mem, _fontSmall);
            if (_error.Length > 0)
            {
                Win32.SetTextColor(mem, (int)Theme.Warn);
                int ey = cy2 + _capH + Scale(18);
                var rErr = new Win32.RECT { L = _pad, T = ey, R = cx - _pad, B = ey + Scale(18) };
                Win32.DrawTextW(mem, _error, -1, ref rErr, Theme.AlignCenter);
            }
            Win32.SetTextColor(mem, (int)Theme.TextSub);
            int by = cy - _pad - Scale(16);
            var rBot = new Win32.RECT { L = _pad, T = by, R = cx - _pad, B = by + Scale(16) };
            Win32.DrawTextW(mem, "Esc 取消  ·  设置保存在 config.json", -1, ref rBot, Theme.AlignCenter);

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
}
