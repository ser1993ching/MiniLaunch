using System.Runtime.InteropServices;

namespace MiniLaunch;

/// DPI 感知与 DPI 查询（对 Win10 1703 以前的系统自动降级）。
/// Win10 1607 才有 GetDpiForWindow，1703 才有 SetProcessDpiAwarenessContext，
/// 更老的系统上直接 P/Invoke 会抛 EntryPointNotFoundException，必须兜住。
public static class Dpi
{
    static bool _aware;

    /// <summary>开启 Per-Monitor V2 DPI 感知；系统不支持时退回 SetProcessDPIAware（Vista+，系统级 DPI 感知）。</summary>
    public static void EnableAwareness()
    {
        if (_aware) return;
        _aware = true;
        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            return;
        }
        catch (EntryPointNotFoundException) { /* Win10 1703 以下 */ }
        catch (DllNotFoundException) { }
        catch (BadImageFormatException) { }
        try { Win32.SetProcessDPIAware(); }
        catch { }
    }

    /// <summary>取窗口所在显示器的 DPI；GetDpiForWindow 不可用时退回 GetDeviceCaps。</summary>
    public static uint ForWindow(IntPtr hwnd)
    {
        try
        {
            uint dpi = Win32.GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi;
        }
        catch (EntryPointNotFoundException) { /* Win10 1607 以下 */ }
        catch (DllNotFoundException) { }
        catch (BadImageFormatException) { }
        return FromDeviceCaps(hwnd);
    }

    /// <summary>没有 GetDpiForWindow 时的兜底：按窗口 DC 的逻辑像素密度取 DPI（供自检验证）。</summary>
    public static uint FromDeviceCaps(IntPtr hwnd)
    {
        IntPtr hdc = Win32.GetDC(hwnd);
        if (hdc == IntPtr.Zero) return 96;
        try
        {
            int x = Win32.GetDeviceCaps(hdc, Win32.LOGPIXELSX);
            int y = Win32.GetDeviceCaps(hdc, Win32.LOGPIXELSY);
            if (x <= 0) x = 96;
            if (y <= 0) y = 96;
            return (uint)((x + y) / 2);
        }
        finally { Win32.ReleaseDC(hwnd, hdc); }
    }
}
