namespace MiniLaunch;

public enum EntrySource { Custom = 0, StartMenu = 1, Registry = 2, Uwp = 3 }

public sealed class Entry
{
    public string Name = "";
    public string LaunchPath = "";     // exe / .lnk / .url 目标 / explorer.exe
    public string Args = "";
    public string WorkDir = "";
    public bool RunAs;
    public string IconRef = "";        // 图标来源文件（可为 .lnk，由 shell 解析到目标图标）
    public int IconIndex;              // ExtractIconEx 用（负数=资源ID）
    public EntrySource Source;
    public string NameLower = "";
    public string HayLower = "";       // 预构建检索串（名称+拼音别名+关键字+路径）
    public string UniqueKey = "";      // 使用统计键：来源+小写名
    public string RevealPath = "";     // “打开所在位置”目标

    public static string NormName(string s) => s.Trim().ToLowerInvariant();

    public string DisplayPath => Source == EntrySource.Uwp ? Args : LaunchPath;
}

public static class EntryMerger
{
    /// <summary>按优先级合并：自定义 > 开始菜单 > 注册表 > UWP，按规范化名称去重。</summary>
    public static List<Entry> Merge(List<Entry> custom, List<Entry> startMenu, List<Entry> registry, List<Entry> uwp)
    {
        var byName = new HashSet<string>();
        var result = new List<Entry>(custom.Count + startMenu.Count + registry.Count + uwp.Count);
        foreach (var e in custom) { if (byName.Add(Entry.NormName(e.Name))) result.Add(e); }
        foreach (var e in startMenu) { if (byName.Add(Entry.NormName(e.Name))) result.Add(e); }
        foreach (var e in registry) { if (byName.Add(Entry.NormName(e.Name))) result.Add(e); }
        foreach (var e in uwp) { if (byName.Add(Entry.NormName(e.Name))) result.Add(e); }
        return result;
    }
}
