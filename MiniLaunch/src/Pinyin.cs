using System.Text;

namespace MiniLaunch;

// 常见中文软件拼音首字母别名表：命中名称片段即附加对应缩写，使 "wx" 能搜到 "微信"
public static class Pinyin
{
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

    public static string Lookup(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder();
        foreach (var (k, v) in Map)
        {
            if (name.Contains(k, StringComparison.Ordinal))
            {
                sb.Append(v).Append(' ');
            }
        }
        return sb.ToString().Trim();
    }
}
