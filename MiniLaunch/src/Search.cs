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
        long ka = IdleKey(a), kb = IdleKey(b);
        if (ka != kb) return kb.CompareTo(ka);
        return string.CompareOrdinal(a.NameLower, b.NameLower);
    }

    long IdleKey(Entry e)
    {
        long c = 0, last = 0;
        if (_usage.Recs.TryGetValue(e.UniqueKey, out var r)) { c = r.Count; last = r.LastTicks; }
        return c * 10_000_000_000L + last;
    }
}
