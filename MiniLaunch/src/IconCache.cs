using System.Runtime.InteropServices;

namespace MiniLaunch;

// 图标提取 + 内存 LRU（上限 64 个 HICON，约 256KB）
public sealed class IconCache : IDisposable
{
    readonly object _lock = new();
    readonly LinkedList<string> _lru = new();
    readonly Dictionary<string, IntPtr> _map = new();
    const int Cap = 64;

    public IntPtr Get(Entry e)
    {
        string key = (e.IconRef ?? "") + "|" + e.IconIndex;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var cached))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return cached;
            }
        }

        IntPtr icon = Extract(e);
        if (icon == IntPtr.Zero) icon = FallbackIcon();

        lock (_lock)
        {
            if (_map.TryGetValue(key, out var cached2))
            {
                // 另一个线程已放入：沿用现有，销毁多余
                if (icon != cached2 && icon != IntPtr.Zero) Win32.DestroyIcon(icon);
                _lru.Remove(key);
                _lru.AddFirst(key);
                return cached2;
            }
            if (_map.Count >= Cap)
            {
                var last = _lru.Last!.Value;
                _lru.RemoveLast();
                if (_map.Remove(last, out var old) && old != IntPtr.Zero) Win32.DestroyIcon(old);
            }
            if (icon != IntPtr.Zero)
            {
                _map[key] = icon;
                _lru.AddFirst(key);
            }
            return icon;
        }
    }

    static IntPtr Extract(Entry e)
    {
        string refPath = e.IconRef;
        if (string.IsNullOrEmpty(refPath)) return IntPtr.Zero;
        try
        {
            if (e.IconIndex != 0)
            {
                var large = new IntPtr[1];
                uint n = Win32.ExtractIconExW(refPath, e.IconIndex, large, null, 1);
                return n > 0 ? large[0] : IntPtr.Zero;
            }
            var fi = new Win32.SHFILEINFOW();
            IntPtr r = Win32.SHGetFileInfoW(refPath, 0, ref fi, (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(),
                Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            return r != IntPtr.Zero ? fi.hIcon : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    static IntPtr FallbackIcon()
    {
        var large = new IntPtr[1];
        uint n = Win32.ExtractIconExW("shell32.dll", 2, large, null, 1);
        return n > 0 ? large[0] : IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var h in _map.Values)
                if (h != IntPtr.Zero) Win32.DestroyIcon(h);
            _map.Clear();
            _lru.Clear();
        }
    }
}
