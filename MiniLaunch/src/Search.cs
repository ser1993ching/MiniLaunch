namespace MiniLaunch;

public sealed class SearchEngine
{
    volatile Entry[] _all = Array.Empty<Entry>();
    readonly UsageDb _usage;

    public SearchEngine(UsageDb usage) => _usage = usage;

    public void SetEntries(List<Entry> entries) => _all = entries.ToArray();

    public int Count => _all.Length;

    /// <summary>分词分隔符：半角空格 / Tab / 全角空格。中文输入法下很容易打出全角空格，
    /// 只认半角的话「微信　会议」这种输入永远搜不到。</summary>
    static readonly char[] TokenSeparators = { ' ', '\t', '\u3000' };

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

        var tokens = q.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
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
        if (!_usage.TryGet(e.UniqueKey, out var r) || r == null || r.Count <= 0) return 0;
        int boost = (int)Math.Min(30, Math.Log2(1 + r.Count) * 12);
        long age = DateTime.UtcNow.Ticks - r.LastTicks;
        if (age < TimeSpan.FromHours(24).Ticks) boost += 15;
        else if (age < TimeSpan.FromDays(7).Ticks) boost += 8;
        return boost;
    }

    int IdleCompare(Entry a, Entry b)
    {
        // 空查询（刚呼出）排序：
        //   1) 使用次数降序
        //   2) 次数相同时「自定义文件夹快捷入口」优先（不再无条件置顶，避免挤掉常用程序）
        //   3) 最近使用时间降序
        //   4) 名称序数比较兜底
        // 设计沿革：早先把自定义文件夹无条件钉在第 0 组，用户配了 4~5 个文件夹时
        // 首屏 8 行全被文件夹占满、常用程序（如 vdi/docker）被挤到看不见，故降为次级键。
        // 注意：Count 不能加权拼进 Ticks——Ticks≈6.4e17，次数权重会被完全淹没
        //（历史 bug，自检有空查询回归断言）。
        _usage.TryGet(a.UniqueKey, out var ra);
        _usage.TryGet(b.UniqueKey, out var rb);
        int ca = ra?.Count ?? 0, cb = rb?.Count ?? 0;
        if (ca != cb) return cb.CompareTo(ca);
        int fa = (a.Source == EntrySource.Custom && a.IsFolder) ? 0 : 1;
        int fb = (b.Source == EntrySource.Custom && b.IsFolder) ? 0 : 1;
        if (fa != fb) return fa.CompareTo(fb);
        long la = ra?.LastTicks ?? 0, lb = rb?.LastTicks ?? 0;
        if (la != lb) return lb.CompareTo(la);
        return string.CompareOrdinal(a.NameLower, b.NameLower);
    }
}
