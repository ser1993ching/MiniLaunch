using System.Runtime.InteropServices;

namespace MiniLaunch;

// 统一视觉：暗色主题配色 + GDI 绘制小工具（渐变 / 圆角 / 按键帽 / 放大镜）
internal static class Theme
{
    // 背景竖向渐变
    public static readonly uint BgTop = Win32.Rgb(0x33, 0x35, 0x3F);   // #33353F
    public static readonly uint BgBottom = Win32.Rgb(0x1C, 0x1D, 0x22); // #1C1D22
    // 搜索框（略深于背景，形成「凹陷」感）
    public static readonly uint Surface = Win32.Rgb(0x2A, 0x2C, 0x36);        // #2A2C36
    public static readonly uint SurfaceBorder = Win32.Rgb(0x3E, 0x42, 0x51);  // #3E4251
    public static readonly uint Divider = Win32.Rgb(0x2E, 0x30, 0x39);
    public static readonly uint FooterLine = Win32.Rgb(0x37, 0x3A, 0x46);
    public static readonly uint Hover = Win32.Rgb(0x33, 0x36, 0x40);
    public static readonly uint SelTop = Win32.Rgb(0x5B, 0x7C, 0xF6);   // #5B7CF6
    public static readonly uint SelBottom = Win32.Rgb(0x3B, 0x5B, 0xDB); // #3B5BDB

    public static readonly uint TextMain = Win32.Rgb(0xF4, 0xF5, 0xF8);
    public static readonly uint TextSub = Win32.Rgb(0xA2, 0xA7, 0xB6);
    public static readonly uint TextDim = Win32.Rgb(0x7E, 0x84, 0x94);
    public static readonly uint TextOnSel = Win32.Rgb(0xEA, 0xEF, 0xFF);
    public static readonly uint TextOnSelSub = Win32.Rgb(0xC7, 0xD3, 0xFF);
    public static readonly uint Accent = Win32.Rgb(0x7C, 0x9C, 0xFF);
    public static readonly uint Warn = Win32.Rgb(0xF0, 0xA6, 0x4B);
    public static readonly uint KeyCapBg = Win32.Rgb(0x3A, 0x3D, 0x47);
    public static readonly uint KeyCapText = Win32.Rgb(0xB9, 0xBE, 0xCC);
    public static readonly uint Thumb = Win32.Rgb(0x6A, 0x70, 0x85);

    // ===== 基础绘制 =====

    /// <summary>竖向线性渐变填充（GDI GradientFill）。</summary>
    public static void GradientV(IntPtr hdc, in Win32.RECT rc, uint top, uint bottom)
    {
        if (rc.R <= rc.L || rc.B <= rc.T) return;
        int n = Marshal.SizeOf<Win32.TRIVERTEX>() * 2 + Marshal.SizeOf<Win32.GRADIENT_RECT>();
        IntPtr buf = Marshal.AllocHGlobal(n);
        try
        {
            var v = new Win32.TRIVERTEX[2];
            v[0].x = rc.L; v[0].y = rc.T;
            v[0].Red = (ushort)((top & 0xFF) * 257);
            v[0].Green = (ushort)(((top >> 8) & 0xFF) * 257);
            v[0].Blue = (ushort)(((top >> 16) & 0xFF) * 257);
            v[0].Alpha = 0;
            v[1].x = rc.R; v[1].y = rc.B;
            v[1].Red = (ushort)((bottom & 0xFF) * 257);
            v[1].Green = (ushort)(((bottom >> 8) & 0xFF) * 257);
            v[1].Blue = (ushort)(((bottom >> 16) & 0xFF) * 257);
            v[1].Alpha = 0;
            var r = new Win32.GRADIENT_RECT { UpperLeft = 0, LowerRight = 1 };
            Marshal.StructureToPtr(v[0], buf, false);
            Marshal.StructureToPtr(v[1], buf + Marshal.SizeOf<Win32.TRIVERTEX>(), false);
            Marshal.StructureToPtr(r, buf + Marshal.SizeOf<Win32.TRIVERTEX>() * 2, false);
            Win32.GradientFill(hdc, buf, 2, buf + Marshal.SizeOf<Win32.TRIVERTEX>() * 2, 1, Win32.GRADIENT_FILL_RECT_V);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>圆角矩形填充（纯色）。radius 为圆角半径（像素）。</summary>
    public static void FillRoundSolid(IntPtr hdc, in Win32.RECT rc, int radius, IntPtr brush)
    {
        int d = Math.Max(1, radius) * 2;
        IntPtr rgn = Win32.CreateRoundRectRgn(rc.L, rc.T, rc.R + 1, rc.B + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        Win32.FillRgn(hdc, rgn, brush);
        Win32.DeleteObject(rgn);
    }

    /// <summary>圆角矩形填充（竖向渐变）：用区域裁剪后画整块渐变。</summary>
    public static void FillRoundGradient(IntPtr hdc, in Win32.RECT rc, int radius, uint top, uint bottom)
    {
        int d = Math.Max(1, radius) * 2;
        IntPtr rgn = Win32.CreateRoundRectRgn(rc.L, rc.T, rc.R + 1, rc.B + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        int saved = Win32.SaveDC(hdc);
        Win32.SelectClipRgn(hdc, rgn);
        GradientV(hdc, rc, top, bottom);
        Win32.RestoreDC(hdc, saved);
        Win32.DeleteObject(rgn);
    }

    /// <summary>1 像素横线。</summary>
    public static void HLine(IntPtr hdc, int x1, int x2, int y, uint color)
    {
        if (x2 <= x1) return;
        var r = new Win32.RECT { L = x1, T = y, R = x2, B = y + 1 };
        IntPtr br = Win32.CreateSolidBrush(color);
        Win32.FillRect(hdc, ref r, br);
        Win32.DeleteObject(br);
    }

    /// <summary>放大镜图标（圆 + 斜柄）。</summary>
    public static void DrawMagnifier(IntPtr hdc, int x, int y, int size, uint color, int thickness)
    {
        IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, thickness, color);
        IntPtr oldPen = Win32.SelectObject(hdc, pen);
        IntPtr oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.NULL_BRUSH));
        int d = (int)(size * 0.72);
        Win32.Ellipse(hdc, x, y, x + d, y + d);
        Win32.MoveToEx(hdc, x + (int)(d * 0.86), y + (int)(d * 0.86), IntPtr.Zero);
        Win32.LineTo(hdc, x + size, y + size);
        Win32.SelectObject(hdc, oldBrush);
        Win32.SelectObject(hdc, oldPen);
        Win32.DeleteObject(pen);
    }

    // ===== 文本 =====

    public const uint AlignLeft = Win32.DT_SINGLELINE | Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX;
    public const uint AlignRight = Win32.DT_SINGLELINE | Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_NOPREFIX;
    public const uint AlignCenter = Win32.DT_SINGLELINE | Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_NOPREFIX;

    /// <summary>测量文本宽度（像素）。</summary>
    public static int MeasureText(IntPtr hdc, IntPtr font, string text)
    {
        IntPtr old = Win32.SelectObject(hdc, font);
        var r = new Win32.RECT { L = 0, T = 0, R = 0, B = 0 };
        Win32.DrawTextW(hdc, text, -1, ref r, Win32.DT_SINGLELINE | Win32.DT_CALCRECT | Win32.DT_NOPREFIX);
        Win32.SelectObject(hdc, old);
        return r.R - r.L;
    }

    /// <summary>画一个「按键帽」（圆角小方块 + 文字），返回下一个可用 x。</summary>
    public static int DrawKeyCap(IntPtr hdc, int x, int y, int h, string text, IntPtr font,
        IntPtr capBrush, uint textColor, int padX)
    {
        int tw = MeasureText(hdc, font, text);
        int w = tw + padX * 2;
        var rc = new Win32.RECT { L = x, T = y, R = x + w, B = y + h };
        FillRoundSolid(hdc, rc, (int)(h * 0.55), capBrush);
        IntPtr old = Win32.SelectObject(hdc, font);
        Win32.SetTextColor(hdc, (int)textColor);
        var tr = new Win32.RECT { L = x, T = y, R = x + w, B = y + h };
        Win32.DrawTextW(hdc, text, -1, ref tr, AlignCenter);
        Win32.SelectObject(hdc, old);
        return x + w;
    }

    /// <summary>Win11 圆角 + 深色标题栏（不支持的版本静默忽略）。</summary>
    public static void ApplyModernFrame(IntPtr hwnd)
    {
        try
        {
            int pref = Win32.DWMWCP_ROUND;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            int dark = 1;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { /* Win10 及更早：忽略 */ }
    }
}
