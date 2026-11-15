using System.Numerics;
using System.Text;

namespace MiniLaunch;

// 拼音支持：手工别名表（多音字/俗称/外语名）+ 通用拼音表（GB2312 一级+二级 6763 字，见 PinyinTable.g.cs）
//
// 生成方式（运行时零依赖、零 NuGet；表由离线脚本产出）：
//     python tools/make_pinyin.py   → MiniLaunch/src/PinyinTable.g.cs
//
// 名字里能搜到的东西（全部拼进 Entry.HayLower，走 55 分档）：
//   「启动器」→ qidongqi（全拼）/ qdq（首字母）/ dongqi、qi（中段）
public static class Pinyin
{
    // 手工别名表：覆盖多音字（重庆/长城）、俗称（计算器→jsq）、外来名（Chrome→ggllq）、
    // 以及表里没有的新软件。命中名称片段即附加别名，优先级高于通用拼音（先拼进检索串）。
    static readonly (string Key, string Alias)[] Map =
    {
        ("微信", "wx weixin chat"),
        ("企业微信", "qywx qiyeweixin wechatwork wework"),
        ("钉钉", "dd dingding"),
        ("腾讯会议", "txhy tengxunhuiyi meeting"),
        ("网易云", "wyy wangyiyun music"),
        ("QQ音乐", "qqyinyue qqmusic"),
        ("酷狗", "kugou kgmusic"),
        ("酷我", "kuwo kwmusic"),
        ("百度网盘", "bdwp baiduwangpan netdisk"),
        ("百度", "baidu"),
        ("阿里云盘", "alyp aliyunpan"),
        ("迅雷", "xl xunlei thunder"),
        ("爱奇艺", "aqy aiqiyi"),
        ("哔哩哔哩", "bilibili bili blbl"),
        ("抖音", "dy douyin"),
        ("快手", "ks kuaishou"),
        ("小红书", "xhs xiaohongshu"),
        ("火狐", "firefox huohu"),
        ("谷歌浏览器", "ggllq chrome googlechrome"),
        ("Chrome", "ggllq chrome"),
        ("Edge", "llq edge"),
        ("浏览器", "llq browser"),
        ("记事本", "jsb notepad"),
        ("计算器", "jsq calc calculator"),
        ("画图", "ht mspaint paint"),
        ("截图", "jietu snip snipping"),
        ("任务管理器", "rwglq taskmgr"),
        ("资源管理器", "zyglq explorer wenjian"),
        ("控制面板", "kzmblm control"),
        ("设置", "sz settings shezhi"),
        ("命令提示符", "mltsf cmd command"),
        ("终端", "zd terminal"),
        ("远程桌面", "yczm mstsc rdp"),
        ("注册表", "zcb regedit"),
        ("设备管理器", "sbglq devmgmt"),
        ("磁盘清理", "cpgn cleanmgr"),
        ("事件查看器", "sjck eventvwr"),
        ("服务", "fuwu services"),
        ("有道", "youdao"),
        ("印象笔记", "yxbj evernote"),
        ("WPS", "wps office bangong"),
        ("办公", "bangong office"),
        ("邮件", "youjian mail"),
        ("日历", "rili calendar"),
        ("照片", "zhaopian photos"),
        ("相机", "xiangji camera"),
        ("音乐", "yinyue music"),
        ("视频", "shipin video"),
        ("下载", "xiazai download"),
        ("笔记", "biji note"),
        ("磁盘", "cipan disk"),
        ("回收站", "huishouzhan recyclebin"),
    };

    // ============ 拼音表（GB2312 6763 字，稠密 9bit 索引 + 位图定位） ============

    static string[]? _syl;                       // 音节表（空格分隔，懒解析）
    static ushort[]? _rankPrefix;                // 位图按字节的前缀 popcount，避免逐字线性求和

    static string[] Syl => _syl ??= PinyinTable.Syllables.Split(' ');

    static ushort[] RankPrefix
    {
        get
        {
            if (_rankPrefix != null) return _rankPrefix;
            var f = PinyinTable.Flags;
            var p = new ushort[f.Length + 1];
            int sum = 0;
            for (int i = 0; i < f.Length; i++)
            {
                p[i] = (ushort)sum;
                sum += BitOperations.PopCount(f[i]);
            }
            p[f.Length] = (ushort)sum;
            return _rankPrefix = p;
        }
    }

    /// <summary>单字拼音音节（无声调，小写）；非 GB2312 汉字/未收录/非汉字 → null。</summary>
    public static string? SyllableOf(char c)
    {
        int off = c - PinyinTable.BlockLo;
        if (off < 0 || off >= PinyinTable.BlockLen) return null;
        if ((PinyinTable.Flags[off >> 3] & (1 << (off & 7))) == 0) return null;
        int id = IndexAt(RankOf(off));
        if (id <= 0) return null;
        var syl = Syl;
        return id <= syl.Length ? syl[id - 1] : null;
    }

    // 位图 [0, off) 内 1 的个数 = 该字在稠密索引表里的序号
    static int RankOf(int off)
    {
        int bytes = off >> 3, rem = off & 7;
        int n = RankPrefix[bytes];
        if (rem != 0) n += BitOperations.PopCount((uint)(PinyinTable.Flags[bytes] & ((1 << rem) - 1)));
        return n;
    }

    // 取第 rank 个 9bit 索引（跨字节读取，最多 3 字节）
    static int IndexAt(int rank)
    {
        const int Bits = PinyinTable.BitsPerIndex;
        int bit = rank * Bits, b = bit >> 3, sh = bit & 7;
        var d = PinyinTable.Ids;
        int v = d[b];
        if (b + 1 < d.Length) v |= d[b + 1] << 8;
        if (b + 2 < d.Length) v |= d[b + 2] << 16;
        return (v >> sh) & ((1 << Bits) - 1);
    }

    /// <summary>表内汉字总数（自检用；应为 6763）。</summary>
    public static int TableCharCount()
    {
        int n = 0;
        for (int i = 0; i < PinyinTable.BlockLen; i++)
            if ((PinyinTable.Flags[i >> 3] & (1 << (i & 7))) != 0) n++;
        return n;
    }

    // ============ 检索串扩展 ============

    /// <summary>
    /// 生成一个名字的拼音检索串：手工别名 + 每段连续汉字的 全拼 / 首字母 / 各后缀（支持中段匹配）。
    /// 非汉字（英文、数字）不产生拼音，仅作为段落分隔。
    /// </summary>
    public static string Expand(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length * 4);

        foreach (var (k, v) in Map)
        {
            if (name.Contains(k, StringComparison.Ordinal)) sb.Append(v).Append(' ');
        }

        int i = 0, n = name.Length;
        var syls = new List<string>(8);
        while (i < n)
        {
            var s = SyllableOf(name[i]);
            if (s == null) { i++; continue; }
            int start = i;
            syls.Clear();
            while (start < n)
            {
                var s2 = SyllableOf(name[start]);
                if (s2 == null) break;
                syls.Add(s2);
                start++;
            }
            AppendSegment(sb, syls);
            i = start;
        }
        return sb.ToString().Trim();
    }

    // 一段连续汉字：全拼、首字母，再加每个后缀（「腾讯会议」→ tengxunhuiyi/txhy, xunhuiyi/xhy, huiyi/hy, yi/y）
    static void AppendSegment(StringBuilder sb, List<string> syls)
    {
        int m = syls.Count;
        if (m == 0) return;
        var full = new StringBuilder(m * 6);
        var ini = new StringBuilder(m);
        foreach (var s in syls)
        {
            full.Append(s);
            ini.Append(s[0]);
        }
        sb.Append(full).Append(' ').Append(ini).Append(' ');

        // 中段匹配：后缀只对不太长的段落生成，避免检索串膨胀
        if (m <= 16)
        {
            for (int k = 1; k < m; k++)
            {
                for (int t = k; t < m; t++) sb.Append(syls[t]);
                sb.Append(' ');
                for (int t = k; t < m; t++) sb.Append(syls[t][0]);
                sb.Append(' ');
            }
        }
    }
}
