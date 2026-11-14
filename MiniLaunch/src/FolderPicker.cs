using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 文件夹选择对话框（SHBrowseForFolder，新式树形对话框）
// 只用 shell32 + ole32，不引 WinForms/WPF；返回值是目录绝对路径，用户取消返回 null。
//
// 坑：shell 的这个对话框要求调用线程是 **STA**。
// .NET 运行时会把主线程初始化成 COM MTA（CoInitializeEx(APARTMENTTHREADED) 会返回
// RPC_E_CHANGED_MODE），在 MTA 线程上调 SHBrowseForFolderW 时对话框会被创建却**始终不显示**
// （WS_VISIBLE 没置上，窗口句柄存在但看不见）。所以这里统一放到自建的 STA 线程执行，
// 调用方线程在等待期间继续泵消息，UI 不会卡死。
public static class FolderPicker
{
    /// <summary>弹出文件夹选择对话框；返回选中目录的绝对路径，取消返回 null。</summary>
    public static string? Pick(IntPtr owner, string title)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return PickCore(owner, title);

        string? result = null;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { result = PickCore(owner, title); }
            catch (Exception ex) { err = ex; }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.FolderPicker",
        };
        t.SetApartmentState(ApartmentState.STA);   // 必须在启动线程前设置
        t.Start();

        // 等待期间继续泵消息：对话框在另一个线程上跑模态循环，本线程不能停摆
        while (t.IsAlive)
        {
            while (Win32.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
        if (err != null) return null;
        return result;
    }

    /// <summary>在 STA 线程上执行真正的对话框调用。</summary>
    static string? PickCore(IntPtr owner, string title)
    {
        // 本线程必须先初始化成 STA，否则 shell 对话框会创建却不显示
        bool comInit = false;
        try { comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0; }
        catch { comInit = false; }
        try
        {
            return PickDialog(owner, title);
        }
        finally
        {
            if (comInit) { try { Win32.CoUninitialize(); } catch { } }
        }
    }

    static string? PickDialog(IntPtr owner, string title)
    {
        IntPtr dispName = IntPtr.Zero;
        try
        {
            // pszDisplayName 由调用方分配（Unicode）：对话框会把用户选中的目录名写回来
            dispName = Marshal.StringToHGlobalUni(new string('\0', 260));
            var bi = new Win32.BROWSEINFO
            {
                hwndOwner = owner,
                pszDisplayName = dispName,
                lpszTitle = title,
                ulFlags = Win32.BIF_RETURNONLYFSDIRS | Win32.BIF_NEWDIALOGSTYLE | Win32.BIF_EDITBOX,
            };
            IntPtr pidl = Win32.SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;   // 用户取消
            try
            {
                var sb = new StringBuilder(260);
                if (!Win32.SHGetPathFromIDListW(pidl, sb, sb.Capacity)) return null;
                string path = sb.ToString().Trim();
                return path.Length == 0 ? null : path;
            }
            finally
            {
                Win32.CoTaskMemFree(pidl);   // PIDL 是 shell 分配的 COM 内存，必须由我们释放
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dispName != IntPtr.Zero) Marshal.FreeHGlobal(dispName);
        }
    }
}
