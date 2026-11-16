namespace MiniLaunch;

// 自绘小窗底部的「已添加条目」标签行（网站窗、文件夹名称窗共用一套实现）。
//
// 交互与编号约定：
//   · 点标签      = 切到该条目继续编辑（名称窗里就是改这个文件夹的显示名）
//   · 点标签右侧 × = 直接删除该条目
//   · 命中编号 >= 100（HitBase），删区再 +100：**不能用负数**，负编号会让
//     `if (hit >= 0)` 判断失灵（按下状态记不进去 → 点了没反应，见坑 #35）。
//   · 放不下的条目不会凭空消失：`Build` 用 out 返回 hidden，窗口右上角显示「另有 N 个未显示」。
//
// 布局只算一次（Build），绘制（Draw）与命中测试（Hit）读同一份结果 —— 否则会出现
// 「看到的位置点不中」（这两个窗的坐标都得跟着 DPI 缩放走，各算一套必然漂移）。
public static class TagRow
{
    /// <summary>一个标签的布局（客户坐标）。</summary>
    public sealed class Tag
    {
        public string Name = "";
        /// <summary>条目唯一键：网站=规范化网址、文件夹=路径（比较时忽略大小写）。</summary>
        public string Key = "";
        public int X, W, Row;
        /// <summary>「×」删除区的宽度（贴在标签最右侧）。</summary>
        public int DelW;
        /// <summary>是否就是当前正在编辑的那一条（高亮用）。</summary>
        public bool IsCurrent;
    }

    /// <summary>命中编号基址：点标签 = HitBase+序号，点 × = DelBase+序号。</summary>
    public const int HitBase = 100;
    public const int DelBase = 200;

    /// <summary>标签最多显示几行（本类默认值；名称窗会传 4 行，见坑 #52）。</summary>
    public const int MaxRows = 2;

    /// <summary>文字宽度估算（中文 12px/字，其余 7px/字）：绘制与命中测试必须共用同一套算法。</summary>
    public static int ApproxWidth(string text, float scale)
    {
        int w = 0;
        foreach (char c in text) w += c > 0x2E80 ? 12 : 7;
        return (int)Math.Round(w * scale);
    }

    /// <summary>按条目列表生成标签布局（不关心被截掉多少时用它）。</summary>
    public static List<Tag> Build(IReadOnlyList<(string Name, string Key)> items, string currentKey,
        int clientW, float scale, int maxRows = MaxRows, float padBase = 22f)
        => Build(items, currentKey, clientW, scale, maxRows, padBase, out _);

    /// <summary>
    /// 按条目列表生成标签布局（items 顺序即显示顺序）。
    /// hidden 返回「因为放不下而没画出来」的条目数：调用方应该把它显示出来（“另有 N 个未显示”），
    /// 否则用户会以为自己的文件夹/网站丢了。
    /// </summary>
    public static List<Tag> Build(IReadOnlyList<(string Name, string Key)> items, string currentKey,
        int clientW, float scale, int maxRows, float padBase, out int hidden)
    {
        hidden = 0;
        var res = new List<Tag>();
        if (items == null) return res;

        int pad = (int)Math.Round(padBase * scale);
        int gap = (int)Math.Round(6 * scale);
        int delW = (int)Math.Round(20 * scale);
        int extra = (int)Math.Round(14 * scale);   // 左右内边距 + 名称与 × 之间的空隙
        int x = pad;
        int row = 0;

        for (int i = 0; i < items.Count; i++)
        {
            var (name, key) = items[i];
            string nm = (name ?? "").Trim();
            if (nm.Length == 0) continue;   // 空名不占位也不算隐藏
            int w = ApproxWidth(nm, scale) + delW + extra;
            if (x + w > clientW - pad)
            {
                row++;
                x = pad;
                if (row >= maxRows)
                {
                    hidden = items.Count - i;   // 剩下的全部没画
                    break;
                }
            }
            res.Add(new Tag
            {
                Name = nm,
                Key = key ?? "",
                X = x,
                W = w,
                Row = row,
                DelW = delW,
                IsCurrent = !string.IsNullOrEmpty(currentKey)
                            && string.Equals(key ?? "", currentKey, StringComparison.OrdinalIgnoreCase),
            });
            x += w + gap;
        }
        return res;
    }

    /// <summary>命中测试：返回 HitBase+i（点标签）/ DelBase+i（点 ×）/ -1。</summary>
    public static int Hit(IReadOnlyList<Tag> tags, int row0Y, int rowH, int rowGap, int mx, int my)
    {
        if (tags == null) return -1;
        int pitch = rowH + rowGap;
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int y0 = row0Y + t.Row * pitch;
            if (my < y0 || my >= y0 + rowH) continue;
            if (mx < t.X || mx >= t.X + t.W) continue;
            return mx >= t.X + t.W - t.DelW ? DelBase + i : HitBase + i;
        }
        return -1;
    }

    /// <summary>画标签行（含上方小标题；空列表时显示 emptyText；hidden>0 时右上角提示还有多少个未显示）。</summary>
    public static void Draw(IntPtr hdc, IReadOnlyList<Tag> tags, int labelY, int row0Y, int rowH, int rowGap,
        int hover, int pressed, IntPtr fontSmall, IntPtr fontHint, int clientW, float scale,
        string label, string emptyText, float padBase = 22f, int hidden = 0)
    {
        int pad = (int)Math.Round(padBase * scale);
        int radius = (int)Math.Round(7 * scale);

        // 小标题 + （放不下时）右对齐提示
        Win32.SelectObject(hdc, fontSmall);
        Win32.SetTextColor(hdc, (int)Theme.TextDim);
        var rLabel = new Win32.RECT { L = pad, T = labelY, R = clientW - pad, B = labelY + (int)Math.Round(18 * scale) };
        Win32.DrawTextW(hdc, label, -1, ref rLabel, Theme.AlignLeft);
        if (hidden > 0)
        {
            Win32.SelectObject(hdc, fontHint);
            Win32.SetTextColor(hdc, (int)Theme.TextDim);
            var rMore = new Win32.RECT { L = pad, T = labelY, R = clientW - pad, B = labelY + (int)Math.Round(18 * scale) };
            Win32.DrawTextW(hdc, $"另有 {hidden} 个未显示", -1, ref rMore, Theme.AlignRight);
        }

        if (tags == null || tags.Count == 0)
        {
            Win32.SelectObject(hdc, fontHint);
            Win32.SetTextColor(hdc, (int)Theme.TextDim);
            var rNone = new Win32.RECT { L = pad, T = row0Y, R = clientW - pad, B = row0Y + rowH };
            Win32.DrawTextW(hdc, emptyText, -1, ref rNone, Theme.AlignLeft);
            return;
        }

        int pitch = rowH + rowGap;
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int y = row0Y + t.Row * pitch;
            bool hovered = hover == HitBase + i || hover == DelBase + i;
            bool hoverDel = hover == DelBase + i;
            bool isPressed = pressed == HitBase + i || pressed == DelBase + i;

            uint bg = isPressed ? Win32.Rgb(0x2C, 0x3A, 0x66)
                    : hovered ? Win32.Rgb(0x33, 0x3F, 0x6B)
                    : Win32.Rgb(0x2C, 0x2F, 0x3A);
            var rc = new Win32.RECT { L = t.X, T = y, R = t.X + t.W, B = y + rowH };
            var outer = new Win32.RECT { L = rc.L - 1, T = rc.T - 1, R = rc.R + 1, B = rc.B + 1 };
            // 先描边（外圈）后底色（内圈）：反过来会把底色整个盖没（坑 #37）
            Theme.FillRoundSolid(hdc, outer, radius + 1,
                Theme.Brush((t.IsCurrent || hovered) ? Theme.Accent : Theme.SurfaceBorder));
            Theme.FillRoundSolid(hdc, rc, radius, Theme.Brush(bg));

            // 名称
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, (int)((hovered || t.IsCurrent) ? Theme.TextMain : Theme.TextSub));
            var tr = new Win32.RECT
            {
                L = t.X + (int)Math.Round(9 * scale),
                T = y,
                R = t.X + t.W - t.DelW,
                B = y + rowH,
            };
            Win32.DrawTextW(hdc, t.Name, -1, ref tr, Theme.AlignLeft);

            // 删除 ×
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, (int)(hoverDel ? Theme.Warn : Theme.TextDim));
            var dr = new Win32.RECT { L = t.X + t.W - t.DelW, T = y, R = t.X + t.W, B = y + rowH };
            Win32.DrawTextW(hdc, "×", -1, ref dr, Theme.AlignCenter);
        }
    }
}
