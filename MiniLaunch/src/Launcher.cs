using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

public static class Launcher
{
    public static bool Run(Entry e, bool runAsOverride = false)
    {
        try
        {
            var info = new Win32.SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFOW>(),
                lpVerb = (runAsOverride || e.RunAs) ? "runas" : "open",
                lpFile = e.LaunchPath,
                lpParameters = string.IsNullOrEmpty(e.Args) ? null : e.Args,
                lpDirectory = string.IsNullOrEmpty(e.WorkDir) ? null : e.WorkDir,
                nShow = Win32.SW_SHOWNORMAL,
            };
            return Win32.ShellExecuteExW(ref info);
        }
        catch
        {
            return false;
        }
    }

    public static void Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        bool ok = File.Exists(path) || Directory.Exists(path);
        if (!ok) return;
        try
        {
            var info = new Win32.SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFOW>(),
                lpFile = "explorer.exe",
                lpParameters = "/select,\"" + path + "\"",
                nShow = Win32.SW_SHOWNORMAL,
            };
            Win32.ShellExecuteExW(ref info);
        }
        catch { }
    }

    public static bool SetClipboardText(IntPtr owner, string text)
    {
        try
        {
            if (!Win32.OpenClipboard(owner)) return false;
            try
            {
                Win32.EmptyClipboard();
                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                IntPtr h = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (h == IntPtr.Zero) return false;
                IntPtr p = Win32.GlobalLock(h);
                if (p == IntPtr.Zero) return false;
                try { Marshal.Copy(bytes, 0, p, bytes.Length); }
                finally { Win32.GlobalUnlock(h); }
                return Win32.SetClipboardData(Win32.CF_UNICODETEXT, h) != IntPtr.Zero;
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }
        catch
        {
            return false;
        }
    }
}
