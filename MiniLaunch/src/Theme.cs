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

    static readonly Dictionary<uint, IntPtr> BrushCache = new();
    static readonly object BrushLock = new();

    /// <summary>
    /// 按颜色复用的实心画刷。绘制路径（每帧的 HLine / 地球图标 / 按钮描边）原本每次
    /// CreateSolidBrush + DeleteObject，属于纯浪费的 GDI 句柄分配；颜色种类是固定的一小撮，
    /// 直接缓存即可。GDI 对象是进程级的，进程退出时系统回收，所以故意不手工释放。
    /// </summary>
    public static IntPtr Brush(uint color)
    {
        lock (BrushLock)
        {
            if (BrushCache.TryGetValue(color, out var h) && h != IntPtr.Zero) return h;
            h = Win32.CreateSolidBrush(color);
            BrushCache[color] = h;
            return h;
        }
    }

    /// <summary>1 像素横线。</summary>
    public static void HLine(IntPtr hdc, int x1, int x2, int y, uint color)
    {
        if (x2 <= x1) return;
        var r = new Win32.RECT { L = x1, T = y, R = x2, B = y + 1 };
        Win32.FillRect(hdc, ref r, Brush(color));
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

    /// <summary>地球图标（网站条目用）：填充圆 + 一条经线椭圆 + 赤道线。</summary>
    public static void DrawGlobe(IntPtr hdc, int x, int y, int size)
    {
        if (size <= 6) return;
        int d = size;
        IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, Math.Max(1, size / 14), Accent);
        IntPtr brush = Brush(Win32.Rgb(0x27, 0x35, 0x5C));
        IntPtr oldPen = Win32.SelectObject(hdc, pen);
        IntPtr oldBrush = Win32.SelectObject(hdc, brush);
        // 圆盘（填充 + 外圈）
        Win32.Ellipse(hdc, x, y, x + d, y + d);
        // 经线与赤道：换成 NULL_BRUSH 只画线
        IntPtr oldNull = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.NULL_BRUSH));
        Win32.Ellipse(hdc, x + d * 28 / 100, y, x + d * 72 / 100, y + d);
        Win32.MoveToEx(hdc, x, y + d / 2, IntPtr.Zero);
        Win32.LineTo(hdc, x + d, y + d / 2);
        Win32.SelectObject(hdc, oldNull);
        Win32.SelectObject(hdc, oldBrush);
        Win32.SelectObject(hdc, oldPen);
        Win32.DeleteObject(pen);   // 画刷走缓存，不删
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

    // ===== 字体 / 按钮（三个自绘窗口共用，避免四处拷贝）=====

    /// <summary>UI 字体名（三处小窗原各自写了一遍字符串）。</summary>
    public const string UiFont = "Microsoft YaHei UI";

    /// <summary>按 DPI 缩放创建字体（调用方负责 DeleteObject）。</summary>
    public static IntPtr MakeFont(string face, int px, int weight, float scale) =>
        Win32.CreateFontW(-(int)Math.Round(px * scale), 0, 0, 0, weight, 0, 0, 0,
            Win32.DEFAULT_CHARSET, Win32.OUT_DEFAULT_PRECIS, Win32.CLIP_DEFAULT_PRECIS,
            Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH | Win32.FF_SWISS, face);

    /// <summary>
    /// 小窗底部按钮布局（客户端坐标）[x, y, w, h] × 2，顺序：取消、确定。
    /// NameDialog / WebDialog 的按钮尺寸完全一致，抽到这里只留一份。
    /// </summary>
    public static int[] DialogButtonLayout(int clientW, int clientH, float scale)
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

    /// <summary>按钮命中测试：0 = 取消、1 = 确定、-1 = 没命中。</summary>
    public static int HitDialogButton(int clientW, int clientH, float scale, int mx, int my)
    {
        var r = DialogButtonLayout(clientW, clientH, scale);
        for (int i = 0; i < 2; i++)
        {
            int x = r[i * 4], y = r[i * 4 + 1], w = r[i * 4 + 2], h = r[i * 4 + 3];
            if (mx >= x && mx < x + w && my >= y && my < y + h) return i;
        }
        return -1;
    }

    /// <summary>画一个自绘按钮：主按钮（确定）用渐变，次按钮（取消）用描边。含悬停/按下反馈。</summary>
    public static void DrawButton(IntPtr hdc, int[] lay, int index, string text, bool primary,
        bool hover, bool pressed, IntPtr font, float scale)
    {
        int x = lay[index * 4], y = lay[index * 4 + 1], w = lay[index * 4 + 2], h = lay[index * 4 + 3];
        int radius = (int)Math.Round(8 * scale);
        int dy = pressed ? 1 : 0;   // 按下时整体下移 1px

        var rc = new Win32.RECT { L = x, T = y + dy, R = x + w, B = y + h + dy };
        if (primary)
        {
            uint top = pressed ? SelBottom : (hover ? Win32.Rgb(0x6E, 0x8C, 0xFF) : SelTop);
            uint bot = pressed ? Win32.Rgb(0x2F, 0x4A, 0xB8) : (hover ? Win32.Rgb(0x4E, 0x6B, 0xE8) : SelBottom);
            FillRoundGradient(hdc, rc, radius, top, bot);
        }
        else
        {
            uint bg = pressed ? Win32.Rgb(0x22, 0x24, 0x2C) : (hover ? Hover : Surface);
            // 圆角描边：先画外扩一圈的边框色，再画内圈底色（顺序反了会把底色盖没，见坑 #37）
            var outer = new Win32.RECT { L = rc.L - 1, T = rc.T - 1, R = rc.R + 1, B = rc.B + 1 };
            FillRoundSolid(hdc, outer, radius + 1, Brush(SurfaceBorder));
            FillRoundSolid(hdc, rc, radius, Brush(bg));
        }

        Win32.SelectObject(hdc, font);
        Win32.SetTextColor(hdc, (int)(primary ? TextOnSel : TextMain));
        var tr = new Win32.RECT { L = rc.L, T = rc.T, R = rc.R, B = rc.B };
        Win32.DrawTextW(hdc, text, -1, ref tr, AlignCenter);
    }

    /// <summary>
    /// 双缓冲绘制：在内存 DC 上画完再一次性 BitBlt 到窗口（三个窗口的 OnPaint 都这么写）。
    /// 内建 try/finally：绘制中途抛异常也不会漏掉 DC/位图（否则就是漏 GDI 句柄）。
    /// </summary>
    public static void BufferedPaint(IntPtr hwnd, Action<IntPtr, int, int> draw)
    {
        IntPtr hdc = Win32.BeginPaint(hwnd, out var ps);
        try
        {
            Win32.GetClientRect(hwnd, out var rc);
            int cx = rc.Width, cy = rc.Height;
            if (cx <= 0 || cy <= 0) return;

            IntPtr mem = Win32.CreateCompatibleDC(hdc);
            IntPtr bmp = Win32.CreateCompatibleBitmap(hdc, cx, cy);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero)
            {
                if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
                if (mem != IntPtr.Zero) Win32.DeleteDC(mem);
                return;
            }
            IntPtr oldBmp = Win32.SelectObject(mem, bmp);
            try
            {
                draw(mem, cx, cy);
                Win32.BitBlt(hdc, 0, 0, cx, cy, mem, 0, 0, Win32.SRCCOPY);
            }
            finally
            {
                Win32.SelectObject(mem, oldBmp);
                Win32.DeleteObject(bmp);
                Win32.DeleteDC(mem);
            }
        }
        finally
        {
            Win32.EndPaint(hwnd, ref ps);
        }
    }

    /// <summary>
    /// 画一个 1px 圆角描边框（先外圈描边色、再内圈底色），返回内圈矩形。
    /// 为什么必须这样画：框里坐的是原生 EDIT 子窗口，它是**方角**的，并且会自己填底色。
    /// 若只画一块实心圆角矩形（或只画外圈）让 EDIT 去盖内部，EDIT 的方角会把四角弧线切掉，
    /// 视觉上就是四个「缺角」。正确做法：父窗口把描边 + 底色都画完，EDIT 再内缩坐在里面。
    /// </summary>
    public static Win32.RECT RoundBorderBox(IntPtr hdc, in Win32.RECT box, int radius, uint bg)
    {
        FillRoundSolid(hdc, box, radius, Brush(SurfaceBorder));
        var inner = new Win32.RECT { L = box.L + 1, T = box.T + 1, R = box.R - 1, B = box.B - 1 };
        FillRoundSolid(hdc, inner, Math.Max(2, radius - 1), Brush(bg));
        return inner;
    }

    /// <summary>
    /// 单行输入框的几何（外框 + 内部 EDIT）。两个自绘小窗共用一套算法，避免「一个缺角、一个偏心」。
    /// · 外框：由调用方用 RoundBorderBox 画 1px 圆角描边；
    /// · EDIT：左右各内缩 3px、上下居中，保证它的方角永远落在内圈圆角弧线之外；
    /// · 垂直居中：单行 EDIT 的文字贴客户区顶部（EM_SETRECT 只对多行有效，见坑 #16），
    ///   所以控件高度取行高（~22px），再整体下移 1px 补偿 EDIT 内 ~3px 的顶距。
    /// </summary>
    public static (Win32.RECT Box, Win32.RECT Edit) SingleLineField(
        int left, int right, int top, int height, float scale)
    {
        int px = Math.Max(1, (int)Math.Round(scale));
        var box = new Win32.RECT { L = left, T = top, R = right, B = top + height };

        int editH = (int)Math.Round(22 * scale);
        int maxH = height - 4 * px;
        if (editH > maxH) editH = Math.Max(px * 4, maxH);   // 框很矮时（小 DPI）至少留出上下边距
        int editTop = (box.T + box.B) / 2 - editH / 2 + px;  // +1px 补偿 EDIT 内部顶距
        int inset = 3 * px;
        var edit = new Win32.RECT { L = box.L + inset, T = editTop, R = box.R - inset, B = editTop + editH };
        return (box, edit);
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
