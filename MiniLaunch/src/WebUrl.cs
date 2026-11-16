namespace MiniLaunch;

/// <summary>
/// 网址处理：补全协议头、校验、取域名、常用站点预设。
/// 规则尽量「宽容」：用户只输入 www.baidu.com 也直接当成 https://www.baidu.com。
/// </summary>
public static class WebUrl
{
    /// <summary>常用站点预设（点一下就填进对话框，用户仍可改名/改网址）。</summary>
    public static readonly (string Name, string Url)[] Presets =
    {
        ("百度", "https://www.baidu.com"),
        ("淘宝", "https://www.taobao.com"),
        ("GitHub", "https://github.com"),
        ("知乎", "https://www.zhihu.com"),
        ("哔哩哔哩", "https://www.bilibili.com"),
    };

    /// <summary>补全协议头：没写协议时按 https 处理；//host 形式补成 https://host。</summary>
    public static string Normalize(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("//", StringComparison.Ordinal)) return "https:" + s;
        // 已经带协议（含非 http 的，如 ftp://）时保持原样，由 IsValid 判定
        if (s.Contains("://", StringComparison.Ordinal)) return s;
        return "https://" + s;
    }

    /// <summary>是否为可用的 http/https 网址。</summary>
    public static bool IsValid(string? raw)
    {
        string s = Normalize(raw);
        if (s.Length == 0) return false;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        int i = s.IndexOf("://", StringComparison.Ordinal);
        string rest = s[(i + 3)..];
        int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
        string host = cut >= 0 ? rest[..cut] : rest;
        if (host.Length == 0) return false;
        if (host.Contains(' ') || host.Contains('\t')) return false;
        // 允许 localhost 与带端口的写法
        if (!host.Contains('.') && !host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>取域名（去掉协议、路径、端口）；取不到时返回空串。</summary>
    public static string HostOf(string? raw)
    {
        string s = Normalize(raw);
        if (s.Length == 0) return "";
        int i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        int cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];
        int at = s.LastIndexOf('@');          // 去掉 user:pass@
        if (at >= 0) s = s[(at + 1)..];
        int colon = s.IndexOf(':');          // 去掉端口
        if (colon >= 0) s = s[..colon];
        return s.Trim();
    }

    /// <summary>默认显示名：优先用域名（去掉 www. 前缀更像名字）。</summary>
    public static string DefaultName(string? url)
    {
        string host = HostOf(url);
        if (host.Length == 0) return "";
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host;
    }
}

/// <summary>网站快捷入口的增删改（与配置存储解耦，便于自检直接验证）。</summary>
public static class WebSites
{
    /// <summary>新增或更新一条网站快捷入口。oldUrl 非空时表示编辑该条目（允许改网址）。</summary>
    public static bool Upsert(List<WebSiteEntry> list, string name, string url, string keywords, string oldUrl = "")
    {
        string u = WebUrl.Normalize(url);
        if (!WebUrl.IsValid(u)) return false;
        string nm = string.IsNullOrWhiteSpace(name) ? WebUrl.DefaultName(u) : name.Trim();

        WebSiteEntry? target = null;
        if (!string.IsNullOrEmpty(oldUrl))
        {
            string old = WebUrl.Normalize(oldUrl);
            foreach (var w in list)
                if (WebUrl.Normalize(w.Url).Equals(old, StringComparison.OrdinalIgnoreCase)) { target = w; break; }
        }
        if (target == null)
        {
            // 同一网址只保留一条：重复添加时改为覆盖名称/关键词
            foreach (var w in list)
                if (WebUrl.Normalize(w.Url).Equals(u, StringComparison.OrdinalIgnoreCase)) { target = w; break; }
        }
        if (target == null)
            list.Add(new WebSiteEntry { Name = nm, Url = u, Keywords = keywords ?? "" });
        else
        {
            target.Name = nm;
            target.Url = u;
            target.Keywords = keywords ?? "";
        }
        return true;
    }

    /// <summary>按网址删除一条网站快捷入口。</summary>
    public static bool Remove(List<WebSiteEntry> list, string url)
    {
        string u = WebUrl.Normalize(url);
        return list.RemoveAll(w => WebUrl.Normalize(w.Url).Equals(u, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
