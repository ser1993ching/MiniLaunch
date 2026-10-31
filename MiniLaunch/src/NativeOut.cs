using System.Text;

namespace MiniLaunch;

// WinExe 无控制台：直接向 stdout 句柄写字节，使 Git Bash / CI 管道可捕获 selftest 输出
internal static class NativeOut
{
    static IntPtr _h;
    static bool _tried;

    static void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try { _h = Win32.GetStdHandle(-11); } catch { _h = IntPtr.Zero; }
    }

    public static void Write(string s)
    {
        try
        {
            Ensure();
            if (_h != IntPtr.Zero && _h != new IntPtr(-1))
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                Win32.WriteFile(_h, b, (uint)b.Length, out _, IntPtr.Zero);
            }
        }
        catch { }
    }

    public static void WriteLine(string s) => Write(s + "\n");
}
