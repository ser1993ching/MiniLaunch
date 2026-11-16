using System.Runtime.InteropServices;

namespace MiniLaunch;

// 图标提取 + 内存 LRU（上限 64 个 HICON，约 256KB）
// 关键约束：提取是重活（SHGetFileInfo 走 shell 解析 .lnk，冷启动每个可达数十毫秒），
// 绝不能在 WM_PAINT 里同步做 —— 故分两条路径：
//   Get(e)    同步取（自检等非 UI 场景用）
//   TryGet/Request  UI 用：命中缓存直接画，未命中则排到后台线程提取，好了就回调刷新
public sealed class IconCache : IDisposable
{
    readonly object _lock = new();
    readonly LinkedList<string> _lru = new();
    readonly Dictionary<string, IntPtr> _map = new();
    readonly HashSet<string> _pending = new();      // 已排队/已提取，等待并入 LRU
    /// <summary>提取失败的 key → 失败时刻（Ticks）。不永久拉黑：程序稍后装好、网络盘恢复后应该还能拿到图标。</summary>
    readonly Dictionary<string, long> _failed = new();
    /// <summary>失败后的冷却期（期内不重试，避免每次重绘都拉一次 shell）。</summary>
    static readonly long FailCooldownTicks = TimeSpan.FromMinutes(10).Ticks;
    readonly Queue<Entry> _queue = new();           // 待提取队列
    readonly Queue<(string Key, IntPtr Icon)> _ready = new(); // 后台已提取，待并入 LRU
    readonly AutoResetEvent _signal = new(false);
    Thread? _worker;
    volatile bool _stop;

    const int Cap = 64;

    /// <summary>后台提取完成时触发（工作线程调用，勿直接碰 UI）。</summary>
    public Action? OnIconReady;

    public IconCache() { }

    static string KeyOf(Entry e) => (e.IconRef ?? "") + "|" + e.IconIndex;

    // ================= UI 用：只读缓存 + 后台排队 =================

    /// <summary>查缓存命中（不触发提取）。</summary>
    public bool TryGet(Entry e, out IntPtr icon)
    {
        string key = KeyOf(e);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out icon))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return true;
            }
        }
        icon = IntPtr.Zero;
        return false;
    }

    /// <summary>请求后台提取（同一 key 只排队一次）。</summary>
    public void Request(Entry e)
    {
        string key = KeyOf(e);
        lock (_lock)
        {
            if (_map.ContainsKey(key) || _pending.Contains(key)) return;
            if (_failed.TryGetValue(key, out long at))
            {
                if (DateTime.UtcNow.Ticks - at < FailCooldownTicks) return;   // 冷却期内不重试
                _failed.Remove(key);                                          // 冷却结束：允许再试
            }
            _pending.Add(key);
            _queue.Enqueue(e);
        }
        EnsureWorker();
        _signal.Set();
    }

    /// <summary>
    /// 把一张图标并入 LRU（调用方必须持有 _lock）。
    /// Get() 与 Pump() 原来各写了一遍「已有则销毁新的 / 满载则驱逐最旧」，合并到这里。
    /// </summary>
    void InsertLocked(string key, IntPtr icon)
    {
        if (_map.TryGetValue(key, out var exist))
        {
            // 已有同 key：沿用旧的，销毁多余的那个
            if (icon != exist && icon != IntPtr.Zero) Win32.DestroyIcon(icon);
            return;
        }
        if (icon == IntPtr.Zero)
        {
            // 彻底失败：记下来（带冷却），避免每次重绘都重新排队拉 shell
            if (_failed.Count > 256) _failed.Clear();
            _failed[key] = DateTime.UtcNow.Ticks;
            return;
        }
        if (_map.Count >= Cap)
        {
            var last = _lru.Last?.Value;
            if (last != null)
            {
                _lru.RemoveLast();
                if (_map.Remove(last, out var old) && old != IntPtr.Zero) Win32.DestroyIcon(old);
            }
        }
        _map[key] = icon;
        _lru.AddFirst(key);
    }

    /// <summary>把后台已提取的图标并入 LRU（必须在 UI 线程调用：销毁 HICON 不能与绘制并发）。</summary>
    public bool Pump()
    {
        bool any = false;
        while (true)
        {
            (string Key, IntPtr Icon) item;
            lock (_lock)
            {
                if (_ready.Count == 0) break;
                item = _ready.Dequeue();
                InsertLocked(item.Key, item.Icon);
                _pending.Remove(item.Key);
            }
            any = true;
        }
        return any;
    }

    // ================= 同步取（自检 / 非 UI 场景） =================

    public IntPtr Get(Entry e)
    {
        if (TryGet(e, out var cached)) return cached;

        IntPtr icon = Extract(e);
        if (icon == IntPtr.Zero) icon = FallbackIcon();

        string key = KeyOf(e);
        lock (_lock)
        {
            InsertLocked(key, icon);
            return _map.TryGetValue(key, out var got) ? got : IntPtr.Zero;
        }
    }

    // ================= 后台提取线程 =================

    void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new Thread(Worker)
        {
            IsBackground = true,
            Name = "MiniLaunch.IconCache",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    void Worker()
    {
        // 工作线程要拉 shell 的 API（SHGetFileInfo → 可能加载 shell 扩展）：
        // 先把本线程初始化成 STA，否则部分扩展会静默失败（图标退化成默认图标）。
        bool comInit = false;
        try { comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0; } catch { }
        try
        {
            WorkerLoop();
        }
        finally
        {
            if (comInit) { try { Win32.CoUninitialize(); } catch { } }
        }
    }

    void WorkerLoop()
    {
        while (!_stop)
        {
            try { _signal.WaitOne(); } catch { break; }
            if (_stop) break;
            while (true)
            {
                Entry e;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    e = _queue.Dequeue();
                }
                IntPtr icon = IntPtr.Zero;
                try
                {
                    icon = Extract(e);
                    if (icon == IntPtr.Zero) icon = FallbackIcon();
                }
                catch { }
                // Dispose 可能已开始（Join 500ms 内没等到线程结束）：不再入队，
                // 否则图标会在 Dispose 清空 _ready 之后才进来，没人销毁它
                if (_stop)
                {
                    try { if (icon != IntPtr.Zero) Win32.DestroyIcon(icon); } catch { }
                    break;
                }
                lock (_lock) { _ready.Enqueue((KeyOf(e), icon)); }
                try { OnIconReady?.Invoke(); } catch { }
            }
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
        _stop = true;
        try { _signal.Set(); } catch { }
        try { _worker?.Join(500); } catch { }
        lock (_lock)
        {
            foreach (var h in _map.Values)
                if (h != IntPtr.Zero) Win32.DestroyIcon(h);
            _map.Clear();
            _lru.Clear();
            // 后台已提取但还没并入的图标也要销毁，避免泄漏
            foreach (var r in _ready)
                if (r.Icon != IntPtr.Zero) Win32.DestroyIcon(r.Icon);
            _ready.Clear();
        }
        try { _signal.Dispose(); } catch { }
    }
}
