namespace MiniLaunch;

public sealed class SearchEngine
{
    volatile Entry[] _all = Array.Empty<Entry>();
    readonly UsageDb _usage;

    public SearchEngine(UsageDb usage) => _usage = usage;

    public void SetEntries(List<Entry> entries) => _all = entries.ToArray();

    public int Count => _all.Length;

    /// <summary>查询：多词 AND；空查询返回常用项。</summary>
    public List<Entry> Query(string q, int max)
    {
        var all = _all;
        q = (q ?? "").Trim();
        if (q.Length == 0)
        {
            var arr = all.ToArray(); // 快照排序
            Array.Sort(arr, IdleCompare);
            var r = new List<Entry>(Math.Min(max, arr.Length));
            for (int i = 0; i < arr.Length && r.Count < max; i++) r.Add(arr[i]);
            return r;
        }

        var tokens = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(Entry E, int S)>();
        foreach (var e in all)
        {
            int total = 0;
            bool miss = false;
            foreach (var t in tokens)
            {
                int s = ScoreToken(e, t);
                if (s < 0) { miss = true; break; }
                total += s;
            }
            if (miss) continue;
            total += UsageBoost(e);
            scored.Add((e, total));
        }
        scored.Sort((a, b) => b.S != a.S ? b.S.CompareTo(a.S) : string.CompareOrdinal(a.E.NameLower, b.E.NameLower));
        var res = new List<Entry>(Math.Min(max, scored.Count));
        for (int i = 0; i < scored.Count && res.Count < max; i++) res.Add(scored[i].E);
        return res;
    }

    static int ScoreToken(Entry e, string t)
    {
        if (e.NameLower.StartsWith(t, StringComparison.Ordinal)) return 100;
        int idx = e.NameLower.IndexOf(t, StringComparison.Ordinal);
        if (idx >= 0) return 80 - Math.Min(idx, 20);
        if (e.HayLower.Contains(t, StringComparison.Ordinal)) return 55;
        return -1;
    }

    int UsageBoost(Entry e)
    {
        if (_usage.Recs.TryGetValue(e.UniqueKey, out var r) && r.Count > 0)
        {
            int boost = (int)Math.Min(30, Math.Log2(1 + r.Count) * 12);
            long age = DateTime.UtcNow.Ticks - r.LastTicks;
            if (age < TimeSpan.FromHours(24).Ticks) boost += 15;
            else if (age < TimeSpan.FromDays(7).Ticks) boost += 8;
            return boost;
        }
        return 0;
    }

    int IdleCompare(Entry a, Entry b)
    {
        // 自定义文件夹快捷入口置顶：「常用文件夹」要在呼出时一眼看到（用户专门配的入口）。
        // 其余条目仍按 (使用次数, 最近使用) 排序，不受本规则影响。
        int fa = (a.Source == EntrySource.Custom && a.IsFolder) ? 0 : 1;
        int fb = (b.Source == EntrySource.Custom && b.IsFolder) ? 0 : 1;
        if (fa != fb) return fa.CompareTo(fb);
        // 先比次数、再比最近时间（设计：(Count, LastTicks) 字典序）。
        // 不能把 Count 加权拼进一个 long：Ticks≈6.4e17，次数权重会被完全淹没，
        // 退化成「最近使用」排序（历史 bug，自检有空查询回归断言）。
        _usage.Recs.TryGetValue(a.UniqueKey, out var ra);
        _usage.Recs.TryGetValue(b.UniqueKey, out var rb);
        int ca = ra?.Count ?? 0, cb = rb?.Count ?? 0;
        if (ca != cb) return cb.CompareTo(ca);
        long la = ra?.LastTicks ?? 0, lb = rb?.LastTicks ?? 0;
        if (la != lb) return lb.CompareTo(la);
        return string.CompareOrdinal(a.NameLower, b.NameLower);
    }
}
