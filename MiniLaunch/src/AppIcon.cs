using System.Runtime.InteropServices;

namespace MiniLaunch;

/// 应用图标加载。
/// 图标由 csc 的 /win32icon 嵌入到程序集，再由 SDK 的 CreateAppHost 复制进 exe（RT_GROUP_ICON 的 ID 固定为 32512）。
/// 注意：单文件发布且开启 PublishReadyToRun 时，SDK 打包器会丢弃 exe 里的图标资源，
/// 此时托盘回退到系统关联图标（见 Tray.LoadAppIcon）。
public static class AppIcon
{
    /// <summary>csc /win32icon 生成的 RT_GROUP_ICON 资源 ID。</summary>
    const int GroupIconId = 32512;

    /// <summary>加载应用图标（默认 32x32），失败返回 IntPtr.Zero；调用方负责 DestroyIcon。</summary>
    public static IntPtr Load()
    {
        try
        {
            IntPtr mod = Win32.GetModuleHandleW(null);
            if (mod == IntPtr.Zero) return IntPtr.Zero;
            return Win32.LoadImageW(mod, (IntPtr)GroupIconId, Win32.IMAGE_ICON, 0, 0, Win32.LR_DEFAULTSIZE);
        }
        catch { return IntPtr.Zero; }
    }
}
