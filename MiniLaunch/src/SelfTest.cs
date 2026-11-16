using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 无头自检：--selftest 运行，结果写 stdout 管道与 selftest-report.txt
public static class SelfTest
{
    static readonly StringBuilder Report = new();
    static int _pass, _fail;

    static void Check(bool cond, string name, string detail = "")
    {
        string line = (cond ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? "  — " + detail : "");
        if (cond) _pass++; else _fail++;
        Report.AppendLine(line);
        NativeOut.WriteLine(line);
    }

    public static int Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string origDir = Paths.Dir;
        string tmpDir = "";
        try
        {
            // 用临时目录隔离，避免污染真实配置
            tmpDir = Path.Combine(Path.GetTempPath(), "MiniLaunch-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Paths.Dir = tmpDir;
            RunAll();
        }
        catch (Exception ex)
        {
            _fail++;
            string line = "[FAIL] 自检异常中断: " + ex.GetType().Name + ": " + ex.Message;
            Report.AppendLine(line);
            NativeOut.WriteLine(line);
        }
        finally
        {
            Paths.Dir = origDir;
            // 清理临时目录（否则每次自检都会在 %TEMP% 留下一个垃圾目录）
            try { if (tmpDir.Length > 0 && Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
            catch { }
        }

        sw.Stop();
        string summary = $"\n===== 自检结果: 通过 {_pass} / 失败 {_fail}，耗时 {sw.ElapsedMilliseconds}ms =====";
        Report.AppendLine(summary);
        NativeOut.WriteLine(summary);

        // 内存快照
        if (Win32.GetProcessWorkingSetSize(Win32.GetCurrentProcess(), out var minWs, out var maxWs))
        {
            string mem = $"当前进程工作集: {maxWs.ToUInt64() / 1024.0 / 1024.0:F1} MB";
            Report.AppendLine(mem);
            NativeOut.WriteLine(mem);
        }

        try
        {
            string reportPath = Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");
            File.WriteAllText(reportPath, Report.ToString());
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    static void RunAll()
    {
        // ---- 1. 热键解析 ----
        var (mods, vk, ok) = HotkeyParser.Parse("Alt+Space");
        Check(ok && mods == Win32.MOD_ALT && vk == Win32.VK_SPACE, "热键解析 Alt+Space");
        var (m2, v2, o2) = HotkeyParser.Parse("Ctrl+Shift+F3");
        Check(o2 && m2 == (Win32.MOD_CONTROL | Win32.MOD_SHIFT) && v2 == 0x72, "热键解析 Ctrl+Shift+F3");
        Check(!HotkeyParser.Parse("乱码键").ok, "热键解析拒绝非法输入");

        // ---- 1c. 开机自启：命令行解析 / 路径判定 / 注册表往返 ----
        Check(AutoStart.ParseExeFromCommand("\"C:\\a b\\MiniLaunch.exe\"") == "C:\\a b\\MiniLaunch.exe",
            "自启命令行解析：带引号路径", AutoStart.ParseExeFromCommand("\"C:\\a b\\MiniLaunch.exe\"") ?? "null");
        Check(AutoStart.ParseExeFromCommand("C:\\x\\MiniLaunch.exe --flag") == "C:\\x\\MiniLaunch.exe",
            "自启命令行解析：带参数时取 exe", AutoStart.ParseExeFromCommand("C:\\x\\MiniLaunch.exe --flag") ?? "null");
        Check(AutoStart.ParseExeFromCommand("") == null && AutoStart.ParseExeFromCommand(null) == null,
            "自启命令行解析：空值返回 null");
        Check(AutoStart.SameExe("\"C:\\X\\MiniLaunch.EXE\"", "c:\\x\\minilaunch.exe"),
            "自启路径比较：忽略大小写与引号");
        Check(AutoStart.IsBuildOutput("E:\\p\\MiniLaunch\\bin\\Debug\\net8.0-windows\\MiniLaunch.exe"),
            "自启目标判定：构建目录视为不稳定");        Check(!AutoStart.IsBuildOutput(AutoStart.InstalledExe),
            "自启目标判定：本地安装目录视为稳定", AutoStart.InstalledExe);

        // 注册表往返：用临时值名，不碰用户真实的 MiniLaunch 项
        const string tmpVal = "MiniLaunch__selftest";
        Check(!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath),
            "Environment.ProcessPath 指向真实可执行文件", Environment.ProcessPath ?? "null");
        string asExe = Environment.ProcessPath ?? "";
        bool w = File.Exists(asExe) && AutoStart.TryEnable(asExe, tmpVal);
        bool rb = w && AutoStart.SameExe(AutoStart.ReadRawCommand(tmpVal) ?? "", asExe);
        AutoStart.TryDisable(tmpVal);
        bool cleaned = AutoStart.ReadRawCommand(tmpVal) == null;
        Check(w, "自启注册表：写入 Run 项成功");
        Check(rb, "自启注册表：写入后回读一致");
        Check(cleaned, "自启注册表：删除后回读为空");

        // 安装到本地：传入临时目录，避免在自检时污染 %LOCALAPPDATA%
        string tmpDir = Path.Combine(Path.GetTempPath(), "MiniLaunch-inst-" + Guid.NewGuid().ToString("N")[..6]);
        string tmpExe = Path.Combine(tmpDir, "fake.exe");
        try
        {
            Directory.CreateDirectory(tmpDir);
            File.WriteAllText(tmpExe, "fake-exe-content");
            var (iok, imsg) = AutoStart.InstallLocal(tmpExe, tmpDir);
            string installed = Path.Combine(tmpDir, "MiniLaunch.exe");
            Check(iok && File.Exists(installed), "安装到本地：复制 exe 到目标目录", imsg);
            bool same = false;
            try { same = File.ReadAllText(installed) == "fake-exe-content"; } catch { }
            Check(same, "安装到本地：内容一致");
        }
        catch { }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
        var (badOk, _) = AutoStart.InstallLocal("C:/nonexistent/x.exe", tmpDir);
        Check(!badOk, "安装到本地：源文件不存在时拒绝安装");

        // ---- 1b. 默认热键 = Ctrl+Space，且可自定义（Format/Parse 往返）----
        var def = new AppConfig();
        Check(def.Hotkey == "Ctrl+Space", "默认热键为 Ctrl+Space", def.Hotkey);
        var (dm, dv, dok) = HotkeyParser.Parse(def.Hotkey);
        Check(dok && dm == Win32.MOD_CONTROL && dv == Win32.VK_SPACE, "默认热键解析为 Ctrl+Space",
            $"mods={dm} vk={dv}");
        Check(HotkeyParser.Format(dm, dv) == "Ctrl+Space", "热键格式化往返 Ctrl+Space",
            HotkeyParser.Format(dm, dv));
        var (cm, cv, cok) = HotkeyParser.Parse("Ctrl+Alt+F8");
        Check(cok && HotkeyParser.Format(cm, cv) == "Ctrl+Alt+F8", "热键格式化往返 Ctrl+Alt+F8",
            HotkeyParser.Format(cm, cv));
        Check(HotkeyParser.IsModifierKey(Win32.VK_CONTROL) && !HotkeyParser.IsModifierKey(0x41),
            "修饰键识别");
        // 回归：Alt+F4 是标准关闭手势，录制窗不能把它当成组合键录进去（否则热键被静默改写）
        Check(HotkeyDialog.IsCloseGesture(Win32.VK_F4, Win32.MOD_ALT), "热键录制：Alt+F4 判定为关闭手势");
        Check(!HotkeyDialog.IsCloseGesture(Win32.VK_F4, Win32.MOD_CONTROL)
              && !HotkeyDialog.IsCloseGesture(Win32.VK_SPACE, Win32.MOD_ALT),
            "热键录制：非 Alt+F4 不误判为关闭手势");

        // ---- 2. 配置原子读写回环 ----
        var cfg = new AppConfig
        {
            Hotkey = "Ctrl+Alt+P",
            AutoStart = true,
            Custom = { new CustomEntry { Name = "测试程序", Path = @"C:\Windows\System32\notepad.exe", Keywords = "jsb note" } },
        };
        ConfigStore.SaveConfig(cfg);
        var cfg2 = ConfigStore.LoadConfig();
        Check(cfg2.Hotkey == "Alt+Space".Replace("Alt+Space", "Ctrl+Alt+P") && cfg2.AutoStart && cfg2.Custom.Count == 1
              && cfg2.Custom[0].Name == "测试程序", "配置保存/加载回环");
        var usage = new UsageDb();
        usage.Recs["x"] = new UsageRec { Count = 3, LastTicks = 123 };
        ConfigStore.SaveUsage(usage);
        var usage2 = ConfigStore.LoadUsage();
        Check(usage2.Recs.Count == 1 && usage2.Recs["x"].Count == 3, "用量库保存/加载回环");

        // ---- 2d. 落盘失败必须吞异常（杀软/索引器短暂锁住 json 时 UI 线程不能崩）----
        try
        {
            using (var fs = new FileStream(Paths.UsagePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ConfigStore.SaveUsage(new UsageDb());   // 目标文件被独占锁定：内部吞掉 IOException，不许抛
            }
            Check(true, "落盘失败吞异常（文件被锁定时不崩溃）");
        }
        catch (Exception ex)
        {
            Check(false, "落盘失败吞异常（文件被锁定时不崩溃）", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { File.Delete(Paths.UsagePath + ".tmp"); } catch { }
            ConfigStore.SaveUsage(usage);   // 恢复测试数据
        }

        // ---- 2e. 用户手改 config.json 写成 "Custom": null 时不能 NRE ----
        File.WriteAllText(Paths.ConfigPath, "{\"Hotkey\":\"Ctrl+Space\",\"Custom\":null}");
        var cfgNull = ConfigStore.LoadConfig();
        Check(cfgNull.Custom != null && cfgNull.Custom.Count == 0, "配置 Custom=null 时自动补空列表");
        Check(Scanners.ScanCustom(cfgNull).Count == 0, "ScanCustom 对空 Custom 列表安全");

        // ---- 2b. 文件夹快捷入口：配置字段 / 旧配置兼容 / 扫描识别 ----
        string folderDir = Path.Combine(Path.GetTempPath(), "MiniLaunch-folder-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folderDir);
        try
        {
            var cfgF = new AppConfig
            {
                Custom =
                {
                    new CustomEntry { Name = "工作", Path = folderDir, IsFolder = true },
                    new CustomEntry { Name = "记事本", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") },
                },
            };
            ConfigStore.SaveConfig(cfgF);
            var cfgF2 = ConfigStore.LoadConfig();
            Check(cfgF2.Custom.Count == 2 && cfgF2.Custom[0].IsFolder && !cfgF2.Custom[1].IsFolder,
                "文件夹标记 IsFolder 写入/读取回环");

            // 旧配置（没有 IsFolder 字段）→ 路径确实是目录时仍要识别为文件夹
            File.WriteAllText(Paths.ConfigPath,
                "{\"Hotkey\":\"Ctrl+Space\",\"Custom\":[{\"Name\":\"旧目录\",\"Path\":\"" +
                folderDir.Replace("\\", "\\\\") + "\"}]}");
            var legacy = ConfigStore.LoadConfig();
            Check(legacy.Custom.Count == 1 && !legacy.Custom[0].IsFolder, "旧配置缺少 IsFolder 字段时默认 false");
            var legacyEntries = Scanners.ScanCustom(legacy);
            Check(legacyEntries.Count == 1 && legacyEntries[0].IsFolder, "旧配置按目录路径自动识别为文件夹");

            var fEntries = Scanners.ScanCustom(cfgF2);
            var fEntry = fEntries.FirstOrDefault(e => e.IsFolder);
            Check(fEntry != null && fEntry.LaunchPath == folderDir && fEntry.Name == "工作",
                "文件夹条目字段完整（名称/路径）");
            Check(fEntry != null && fEntry.IconRef == folderDir, "文件夹条目图标来源为目录本身");
            Check(fEntries.Any(e => !e.IsFolder && e.Name == "记事本"), "普通程序条目不被误判为文件夹");

            // 空查询：文件夹不再无条件置顶（用户配了 4~5 个文件夹时会占满首屏、把常用程序挤下去），
            // 改为「次数相同时文件夹优先」——这里用空用量库（两边次数都是 0）验证次级键生效。
            var engF = new SearchEngine(new UsageDb());
            engF.SetEntries(fEntries);
            var idleF = engF.Query("", 10);
            Check(idleF.Count >= 2 && idleF[0].IsFolder && idleF[0].Name == "工作",
                "空查询次数相同时文件夹优先", $"r=[{string.Join(",", idleF.Select(e => e.Name))}]");

            // 本次改动的主诉求：常用程序必须排在未使用的文件夹前面
            var uIdleF = new UsageDb();
            var appEntry = fEntries.First(e => !e.IsFolder);
            uIdleF.Recs[appEntry.UniqueKey] = new UsageRec { Count = 3, LastTicks = DateTime.UtcNow.Ticks };
            var engF2 = new SearchEngine(uIdleF);
            engF2.SetEntries(fEntries);
            var idleF2 = engF2.Query("", 10);
            Check(idleF2.Count >= 2 && ReferenceEquals(idleF2[0], appEntry) && !idleF2[0].IsFolder,
                "空查询常用程序优先于未使用文件夹", $"r=[{string.Join(",", idleF2.Select(e => e.Name))}]");
            // 按别名搜索
            var hitF = engF.Query("工作", 10);
            Check(hitF.Count == 1 && hitF[0].IsFolder, "按别名搜索到文件夹条目");
            // 按目录名搜索（别名与真实目录名不同时也能搜到）
            var hitF2 = engF.Query(new DirectoryInfo(folderDir).Name.ToLowerInvariant(), 10);
            Check(hitF2.Count >= 1, "按真实目录名也能搜到文件夹");

            // 目录图标：SHGetFileInfo 对目录要能拿到「文件夹」图标（否则会退化成文档图标）
            var fiF = new Win32.SHFILEINFOW();
            IntPtr rF = Win32.SHGetFileInfoW(folderDir, 0, ref fiF,
                (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            Check(rF != IntPtr.Zero && fiF.hIcon != IntPtr.Zero, "目录图标可提取（SHGetFileInfo）",
                $"r={rF} hIcon={fiF.hIcon}");
            if (fiF.hIcon != IntPtr.Zero) Win32.DestroyIcon(fiF.hIcon);
        }
        finally
        {
            try { Directory.Delete(folderDir, true); } catch { }
        }

        // ---- 2c. 名称输入窗按钮 / 现代文件夹选择对话框 ----
        // 名称窗以前只能按回车，现在有自绘的「取消 / 确定」按钮，点击即确认
        var bl = NameDialog.ButtonLayout(460, 214, 1f);
        bool btnsInside = bl[0] >= 0 && bl[1] >= 0 && bl[4] + bl[6] <= 460 && bl[5] + bl[7] <= 214
                          && bl[4] > bl[0] + bl[2];   // 确定在取消右侧，且都在窗口内
        Check(btnsInside, "名称输入窗：按钮布局在窗口内（取消在左、确定在右）",
            $"cancel=({bl[0]},{bl[1]},{bl[2]}x{bl[3]}) ok=({bl[4]},{bl[5]},{bl[6]}x{bl[7]})");
        Check(NameDialog.HitButton(460, 214, 1f, bl[4] + bl[6] / 2, bl[5] + bl[7] / 2) == NameDialog.BtnOk,
            "名称输入窗：点击「确定」按钮命中");
        Check(NameDialog.HitButton(460, 214, 1f, bl[0] + bl[2] / 2, bl[1] + bl[3] / 2) == NameDialog.BtnCancel,
            "名称输入窗：点击「取消」按钮命中");
        Check(NameDialog.HitButton(460, 214, 1f, 5, 5) == -1,
            "名称输入窗：空白处不误命中按钮");
        // vtable 槽位对齐回归：GetResult 必须落在槽 20（未显示时返回 E_UNEXPECTED）
        Check(FolderPicker.CanProbeResultSlot(),
            "文件夹选择：IFileDialog 槽位对齐（GetResult 槽位探测）",
            $"hr=0x{FolderPicker.LastResultSlotHr:X8} {FolderPicker.LastProbeError}");
        bool modernOk = FolderPicker.CanCreateModernDialog();
        Check(modernOk,
            "文件夹选择：现代（资源管理器风格）对话框 IFileDialog 可创建",
            $"hr=0x{FolderPicker.LastCreateHr:X8}");

        // ---- 2f. 网站快捷入口：配置 / 网址规范化 / 扫描 / 搜索 / 合并优先级 ----
        var cfgW = new AppConfig
        {
            Websites =
            {
                new WebSiteEntry { Name = "百度", Url = "www.baidu.com", Keywords = "搜索" },
                new WebSiteEntry { Name = "GitHub", Url = "https://github.com/", Keywords = "代码 git" },
                new WebSiteEntry { Name = "无效站点", Url = "not a url" },
            },
        };
        ConfigStore.SaveConfig(cfgW);
        var cfgW2 = ConfigStore.LoadConfig();
        Check(cfgW2.Websites.Count == 3, "网站配置保存/加载回环", $"count={cfgW2.Websites.Count}");

        var webEntries = Scanners.ScanWebsites(cfgW2);
        Check(webEntries.Count == 2, "网站扫描：无效网址被丢弃", $"count={webEntries.Count}");
        var bd = webEntries.FirstOrDefault(e => e.Name == "百度");
        Check(bd != null && bd.IsWeb && bd.LaunchPath == "https://www.baidu.com",
            "网站网址自动补全 https 协议", bd?.LaunchPath ?? "null");
        Check(bd != null && bd.RevealPath.Length == 0, "网站条目没有「打开所在位置」目标");
        Check(bd != null && bd.HayLower.Contains("baidu.com"), "网站检索串包含域名", bd?.HayLower ?? "");

        Check(WebUrl.IsValid("https://www.baidu.com") && WebUrl.IsValid("www.baidu.com")
              && !WebUrl.IsValid("not a url") && !WebUrl.IsValid(""), "网址校验");
        Check(WebUrl.HostOf("https://www.baidu.com/s?wd=x") == "www.baidu.com",
            "网址取域名", WebUrl.HostOf("https://www.baidu.com/s?wd=x"));
        Check(WebUrl.DefaultName("https://www.taobao.com") == "taobao.com",
            "默认名称取域名（去 www.）", WebUrl.DefaultName("https://www.taobao.com"));
        Check(WebUrl.Presets.Any(p => p.Url.Contains("baidu.com")), "常用站点预设含百度");

        // 搜索：名称 / 拼音 / 域名 / 关键词 都能命中
        var engW = new SearchEngine(new UsageDb());
        engW.SetEntries(webEntries);
        Check(engW.Query("baidu", 5).Count == 1, "按域名搜索网站条目");
        Check(engW.Query("bd", 5) is { Count: 1 } qw && qw[0].Name == "百度", "拼音首字母搜索网站条目");
        Check(engW.Query("搜索", 5).Count == 1, "按关键词搜索网站条目");

        // 合并优先级：网站条目压过同名的开始菜单条目
        var eWebSame = Scanners.Make("百度", "https://www.baidu.com", "", EntrySource.Web, "", isWeb: true);
        var eAppSame = Scanners.Make("百度", @"C:\x\baidu.exe", "", EntrySource.StartMenu, @"C:\x\baidu.exe");
        var mergedW = EntryMerger.Merge(new(), new List<Entry> { eAppSame }, new(), new(), new List<Entry> { eWebSame });
        Check(mergedW.Count == 1 && mergedW[0].IsWeb, "同名时网站条目优先于开始菜单条目");

        // 用户手改 config.json 写成 "Websites": null 时不能 NRE
        File.WriteAllText(Paths.ConfigPath, "{\"Hotkey\":\"Ctrl+Space\",\"Websites\":null}");
        var cfgNullW = ConfigStore.LoadConfig();
        Check(cfgNullW.Websites != null && cfgNullW.Websites.Count == 0, "配置 Websites=null 时自动补空列表");
        Check(Scanners.ScanWebsites(cfgNullW).Count == 0, "ScanWebsites 对空 Websites 列表安全");

        // 网站编辑窗：按钮与预设标签的命中区
        var wbl = WebDialog.ButtonLayout(470, 340, 1f);
        Check(wbl[0] >= 0 && wbl[4] + wbl[6] <= 470 && wbl[5] + wbl[7] <= 340,
            "网站编辑窗：按钮布局在窗口内（取消在左、确定在右）",
            $"cancel=({wbl[0]},{wbl[1]}) ok=({wbl[4]},{wbl[5]})");
        Check(WebDialog.HitButton(470, 340, 1f, wbl[4] + wbl[6] / 2, wbl[5] + wbl[7] / 2) == WebDialog.BtnOk,
            "网站编辑窗：点击「确定」按钮命中");
        int chipW = WebDialog.ChipWidth(WebUrl.Presets[0].Name, 1f);
        Check(WebDialog.HitPreset(470, 1f, 22 + chipW / 2, WebDialog.PresetTop(1f) + 10) == 0,
            "网站编辑窗：常用站点标签命中");
        Check(WebDialog.HitPreset(470, 1f, 5, 5) == -1, "网站编辑窗：空白处不误命中标签");

        // 网站条目的增删改（与 UI 解耦，直接验证）
        var wsList = new List<WebSiteEntry>();
        Check(WebSites.Upsert(wsList, "百度", "www.baidu.com", "搜索"), "新增网站条目");
        Check(wsList.Count == 1 && wsList[0].Url == "https://www.baidu.com" && wsList[0].Name == "百度",
            "新增网站条目：网址自动补协议头", wsList.Count > 0 ? wsList[0].Url : "");
        Check(WebSites.Upsert(wsList, "百度一下", "www.baidu.com", "搜索2"), "同一网址重复添加不新增条目");
        Check(wsList.Count == 1 && wsList[0].Name == "百度一下" && wsList[0].Keywords == "搜索2",
            "同一网址重复添加时覆盖名称与关键词");
        Check(WebSites.Upsert(wsList, "淘宝", "https://www.taobao.com", ""), "新增第二个网站条目");
        Check(!WebSites.Upsert(wsList, "旧名", "not a url", ""), "无效网址拒绝写入");
        Check(wsList.Count == 2, "无效网址不改变条目数", $"count={wsList.Count}");
        Check(WebSites.Upsert(wsList, "淘宝商城", "https://www.taobao.com/x", "购物",
                "https://www.taobao.com"), "编辑网站条目（允许改网址）");
        Check(wsList.Count == 2 && wsList.Any(w => w.Url == "https://www.taobao.com/x" && w.Name == "淘宝商城"),
            "编辑网站条目生效");
        Check(WebSites.Remove(wsList, "https://www.baidu.com") && wsList.Count == 1, "按网址删除网站条目");
        Check(!WebSites.Remove(wsList, "https://www.baidu.com"), "删除不存在的网址返回 false");

        // ---- 3. 拼音：手工别名 + GB2312 全字集通用拼音表 ----
        Check(Pinyin.Expand("微信").Contains("wx"), "拼音别名 微信→wx");
        Check(Pinyin.Expand("企业微信").Contains("qywx"), "拼音别名 企业微信→qywx");
        Check(Pinyin.Expand("计算器").Contains("jsq"), "拼音别名 计算器→jsq");

        // 通用拼音表（tools/make_pinyin.py 生成）：一级汉字全拼/首字母/中段
        Check(Pinyin.TableCharCount() == 6763, "拼音表覆盖 GB2312 全字集 6763 字", $"count={Pinyin.TableCharCount()}");
        Check(Pinyin.SyllableOf('启') == "qi", "拼音表 启→qi", $"got={Pinyin.SyllableOf('启')}");
        Check(Pinyin.SyllableOf('淼') == "miao", "拼音表 二级汉字 淼→miao", $"got={Pinyin.SyllableOf('淼')}");
        Check(Pinyin.SyllableOf('鑫') == "xin", "拼音表 二级汉字 鑫→xin", $"got={Pinyin.SyllableOf('鑫')}");
        Check(Pinyin.SyllableOf('A') == null && Pinyin.SyllableOf((char)0x3000) == null,
            "拼音表 非汉字返回 null（不越界）");
        var expQD = Pinyin.Expand("启动器");
        Check(expQD.Contains("qidongqi") && expQD.Contains("qdq"), "拼音扩展 启动器→qidongqi/qdq", expQD);
        var expDC = Pinyin.Expand("电磁计算");
        Check(expDC.Contains("diancijisuan") && expDC.Contains("dcjs"), "拼音扩展 电磁计算→diancijisuan/dcjs", expDC);
        var expHY = Pinyin.Expand("腾讯会议");
        Check(expHY.Contains("hy") && expHY.Contains("huiyi"), "拼音扩展 支持中段（会议→hy/huiyi）", expHY);
        Check(Pinyin.Expand("docker desktop").Length == 0, "拼音扩展 纯英文名不产生拼音串");

        // 搜索层：拼音能搜到中文文件夹名 / 中段
        var uPy = new UsageDb();
        var engPy = new SearchEngine(uPy);
        var eDc = Scanners.Make("电磁计算", @"D:\Desktop\电磁计算", "", EntrySource.Custom, @"D:\Desktop\电磁计算", isFolder: true);
        var eQD = Scanners.Make("启动器", @"E:\code\启动器", "", EntrySource.Custom, @"E:\code\启动器", isFolder: true);
        var eHy = Scanners.Make("腾讯会议", @"C:\x\wemeet.exe", "", EntrySource.StartMenu, @"C:\x\wemeet.exe");
        var eDk = Scanners.Make("Docker Desktop", @"C:\x\docker.exe", "", EntrySource.StartMenu, @"C:\x\docker.exe");
        engPy.SetEntries(new List<Entry> { eDc, eQD, eHy, eDk });
        Check(engPy.Query("dcjs", 5) is { Count: 1 } q1 && ReferenceEquals(q1[0], eDc), "拼音搜索 dcjs→电磁计算");
        Check(engPy.Query("qidongqi", 5) is { Count: 1 } q2 && ReferenceEquals(q2[0], eQD), "全拼搜索 qidongqi→启动器");
        Check(engPy.Query("hy", 5) is { Count: 1 } q3 && ReferenceEquals(q3[0], eHy), "拼音中段 hy→腾讯会议");
        Check(engPy.Query("docker", 5) is { Count: 1 } q4 && ReferenceEquals(q4[0], eDk), "拼音表不影响英文名搜索");

        // 拼音扩展性能：扫描 267 条目时开销要可忽略（留足余量，避免机器惊动误报）
        var swPy = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Pinyin.Expand("腾讯会议启动器电磁计算网易云音乐");
        swPy.Stop();
        Check(swPy.ElapsedMilliseconds < 200, "拼音扩展 200 次调用 < 200ms", $"{swPy.ElapsedMilliseconds}ms");

        // ---- 4. 扫描器：开始菜单 ----
        var sm = Scanners.ScanStartMenu();
        Check(sm.Count > 0, "开始菜单扫描有条目", $"count={sm.Count}");
        Check(sm.All(e => e.Name.Length > 0 && e.LaunchPath.Length > 0), "开始菜单条目字段完整");
        Check(sm.All(e => !e.NameLower.StartsWith("卸载") && !e.NameLower.StartsWith("uninstall")), "开始菜单过滤卸载项");

        // ---- 5. 扫描器：注册表 ----
        var reg = Scanners.ScanRegistry();
        Check(reg.Count > 0, "注册表扫描有条目", $"count={reg.Count}");
        Check(reg.All(e => !e.Name.ToLowerInvariant().Contains("redistributable")), "注册表过滤运行库噪声");
        Check(reg.All(e => File.Exists(e.LaunchPath)), "注册表条目目标存在");

        // ---- 6. 扫描器：UWP（powershell 可能不可用，容错） ----
        int uwpCount = 0;
        try { uwpCount = Scanners.ScanUwp(true).Count; }
        catch (Exception ex) { Check(false, "UWP 扫描无异常", ex.Message); }
        var (uwpCacheNow, _) = Scanners.ReadUwpCache();
        Check(uwpCount == uwpCacheNow.Count, "UWP 扫描执行且与磁盘缓存一致", $"count={uwpCount} cache={uwpCacheNow.Count}");

        // ---- 6b. UWP JSON 解析容错（BOM / 控制字符 / 孤立代理项）----
        string j1 = "\uFEFF[{\"Name\":\"A\u0001B\",\"AppID\":\"X_1!App\"}]";
        var parsed1 = Scanners.ParseStartAppsJson(j1);
        Check(parsed1 != null && parsed1.Count == 1 && parsed1[0].AppId == "X_1!App",
            "UWP JSON 解析容错（BOM + 控制字符）", parsed1 == null ? "null" : $"count={parsed1.Count}");
        string j2 = "[{\"Name\":\"C\uD800D\",\"AppID\":\"Y_1!App\"}]";
        var parsed2 = Scanners.ParseStartAppsJson(j2);
        Check(parsed2 != null && parsed2.Count == 1 && parsed2[0].AppId == "Y_1!App",
            "UWP JSON 解析容错（孤立代理项）", parsed2 == null ? "null" : $"count={parsed2.Count}");

        // ---- 6c. UWP 缓存能落盘并被复用（修复前因管道编码错误永远写不出缓存）----
        var (cacheItems, cacheTicks) = Scanners.ReadUwpCache();
        Check(cacheTicks > 0, "UWP 缓存已落盘（含扫描时间戳）", $"ticks={cacheTicks} items={cacheItems.Count}");
        if (cacheTicks > 0)
        {
            var cachedEntries = Scanners.ScanUwp(false); // 应直接走磁盘缓存，不再拉 powershell
            Check(cachedEntries.Count == cacheItems.Count, "UWP 缓存复用（不再重复拉 powershell）",
                $"entries={cachedEntries.Count} cache={cacheItems.Count}");
        }
        else
        {
            Check(true, "UWP 缓存复用（本机无法运行 powershell，跳过校验）");
        }

        // ---- 7. 合并去重 ----
        var e1 = Scanners.Make("Chrome", @"C:\x\chrome.exe", "", EntrySource.StartMenu, @"C:\x\chrome.exe");
        var e2 = Scanners.Make("chrome", @"C:\y\chrome2.exe", "", EntrySource.Registry, @"C:\y\chrome2.exe");
        var merged = EntryMerger.Merge(new(), new List<Entry> { e1 }, new List<Entry> { e2 }, new());
        Check(merged.Count == 1 && merged[0].Source == EntrySource.StartMenu, "合并去重且优先级正确");

        // ---- 8. 搜索 ----
        var u = new UsageDb();
        var eng = new SearchEngine(u);
        var a = Scanners.Make("Google Chrome", @"C:\x\chrome.exe", "", EntrySource.StartMenu, @"C:\x\chrome.exe");
        var b = Scanners.Make("Chrome Canary", @"C:\y\canary.exe", "", EntrySource.StartMenu, @"C:\y\canary.exe");
        var c = Scanners.Make("微信", @"C:\z\wechat.exe", "", EntrySource.Custom, @"C:\z\wechat.exe");
        eng.SetEntries(new List<Entry> { a, b, c });

        var r1 = eng.Query("chrome", 10);
        Check(r1.Count == 2 && r1[0] == b && r1[1] == a, "前缀匹配优先于包含匹配", $"r1=[{string.Join(",", r1.Select(e => e.Name))}]");
        var r2 = eng.Query("wx", 10);
        Check(r2.Count == 1 && r2[0] == c, "拼音缩写命中");
        var r3 = eng.Query("zzzz不存在的词", 10);
        Check(r3.Count == 0, "无结果返回空");
        var r4 = eng.Query("", 10);
        Check(r4.Count == 3, "空查询返回全部");
        var r5 = eng.Query("chrome canary", 10);
        Check(r5.Count == 1 && r5[0] == b, "多词 AND 查询");

        // 频率加权
        u.Recs[b.UniqueKey] = new UsageRec { Count = 50, LastTicks = DateTime.UtcNow.Ticks };
        var r6 = eng.Query("chrome", 10);
        Check(r6.Count == 2 && r6[0] == b, "使用频率提升排序");

        // 回归：空查询必须「频率优先」而非「最近使用优先」
        // （历史 bug：Count 权重拼进 Ticks 的 long 被淹没，30 天前用过 100 次的会输给今天用过 1 次的）
        var uIdle = new UsageDb();
        var engIdle = new SearchEngine(uIdle);
        var eOld = Scanners.Make("老牌常用", @"C:\x\old.exe", "", EntrySource.StartMenu, @"C:\x\old.exe");
        var eNew = Scanners.Make("新近偶尔", @"C:\y\new.exe", "", EntrySource.StartMenu, @"C:\y\new.exe");
        uIdle.Recs[eOld.UniqueKey] = new UsageRec { Count = 100, LastTicks = DateTime.UtcNow.Ticks - TimeSpan.FromDays(30).Ticks };
        uIdle.Recs[eNew.UniqueKey] = new UsageRec { Count = 1, LastTicks = DateTime.UtcNow.Ticks };
        engIdle.SetEntries(new List<Entry> { eNew, eOld });
        var rIdle = engIdle.Query("", 10);
        Check(rIdle.Count == 2 && rIdle[0] == eOld, "空查询频率优先于最近使用", $"r=[{string.Join(",", rIdle.Select(e => e.Name))}]");

        // ---- 9. 图标提取 ----
        using (var icons = new IconCache())
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var ne = Scanners.Make("记事本", notepad, "", EntrySource.Custom, notepad);
            IntPtr h = icons.Get(ne);
            Check(h != IntPtr.Zero, "图标提取 notepad.exe");
            IntPtr h2 = icons.Get(ne);
            Check(h2 == h, "图标 LRU 缓存命中");
        }

        // ---- 9b. 图标后台提取（UI 线程不再同步取图标，避免首屏卡顿）----
        using (var icons = new IconCache())
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var ne = Scanners.Make("记事本", "notepad.exe", "", EntrySource.Custom, notepad);
            Check(!icons.TryGet(ne, out _), "图标缓存初始为空");
            icons.Request(ne);
            bool got = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                icons.Pump();
                if (icons.TryGet(ne, out var hi) && hi != IntPtr.Zero) { got = true; break; }
                Thread.Sleep(10);
            }
            Check(got, "图标后台提取后可被 UI 取用");
            icons.Request(ne); // 已在缓存：不应重复排队
            Check(icons.TryGet(ne, out var h3) && h3 != IntPtr.Zero, "图标缓存命中后不再重复提取");
        }

        // ---- 10. 扫描结果完整性 ----
        var full = EntryMerger.Merge(Scanners.ScanCustom(cfg2), sm, reg, new());
        Check(full.Count >= 10, "合并后总条目数量合理", $"count={full.Count}");
        Check(full.All(e => e.HayLower.Length > 0 && e.UniqueKey.Length > 0), "条目检索字段已构建");

        // ---- 11. 文本封送：GetWindowTextW 必须走 Unicode ----
        // 回归防护：漏 CharSet.Unicode 时 "docker" 会被读成 "d"（只能按首字母搜索）
        IntPtr he = Win32.CreateWindowExW(0, "EDIT", "", Win32.WS_POPUP, 0, 0, 200, 40,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (he != IntPtr.Zero)
        {
            const string sample = "docker 微信";
            Win32.SetWindowTextW(he, sample);
            var tsb = new StringBuilder(256);
            Win32.GetWindowTextW(he, tsb, 256);
            Check(tsb.ToString() == sample, "GetWindowTextW 多字符不被截断", $"读回“{tsb}”");
            Win32.DestroyWindow(he);
        }
        else Check(false, "创建 EDIT 用于文本封送回归", "CreateWindowExW 失败");

        // ---- 12. 应用图标（内嵌于 exe，资源管理器 / 任务栏 / 托盘共用）----
        string? selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe))
        {
            uint iconCount = Win32.ExtractIconExW(selfExe, -1, null, null, 0);
            Check(iconCount > 0, "exe 内嵌应用图标可提取", $"图标数={iconCount}");

            // 托盘图标即取自自身 exe（见 Tray.MakeNid），此处确认该链路可用
            var fi = new Win32.SHFILEINFOW();
            IntPtr r = Win32.SHGetFileInfoW(selfExe, 0, ref fi,
                (uint)Marshal.SizeOf<Win32.SHFILEINFOW>(),
                Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            Check(r != IntPtr.Zero && fi.hIcon != IntPtr.Zero, "托盘图标来源可用（SHGetFileInfo 取自身 exe 图标）");
            if (fi.hIcon != IntPtr.Zero) Win32.DestroyIcon(fi.hIcon);
        }
        else Check(false, "获取自身可执行文件路径");

        // ---- 13. 应用图标加载（exe 模块内的 Win32 图标资源）----
        // 单文件发布且开启 PublishReadyToRun 时 SDK 会丢弃图标资源，此断言能第一时间发现
        IntPtr appIcon = AppIcon.Load();
        Check(appIcon != IntPtr.Zero, "应用图标：exe 模块资源可加载");
        if (appIcon != IntPtr.Zero) Win32.DestroyIcon(appIcon);

        // ---- 14. DPI 兼容兜底（Win10 1703 以前无 SetProcessDpiAwarenessContext / 1607 以前无 GetDpiForWindow）----
        try
        {
            Dpi.EnableAwareness();
            Check(true, "DPI 感知设置不抛异常");
        }
        catch (Exception ex) { Check(false, "DPI 感知设置不抛异常", ex.GetType().Name); }

        IntPtr hDpi = Win32.CreateWindowExW(0, "STATIC", "", Win32.WS_POPUP, 0, 0, 10, 10,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (hDpi != IntPtr.Zero)
        {
            uint dpi1 = Dpi.ForWindow(hDpi);
            uint dpi2 = Dpi.FromDeviceCaps(hDpi);
            Check(dpi1 >= 48 && dpi1 <= 480, "窗口 DPI 读取合理", $"GetDpiForWindow={dpi1}");
            Check(dpi2 >= 48 && dpi2 <= 480, "DPI 兜底路径（GetDeviceCaps）可用", $"={dpi2}");
            Win32.DestroyWindow(hDpi);
        }
        else Check(false, "创建窗口用于 DPI 自检");

        // ---- 15. 主窗口布局：客户区必须等于 DPI 重算后的尺寸 ----
        // 回归：窗口按 96 DPI 创建后只在 WM_CREATE 重算度量却不 resize，
        // 高 DPI 主屏下底部行/底栏被裁剪（本机 96 DPI 时此断言平凡通过，高 DPI 机器上才有鉴别力）
        var mw = MainWindow.Create(new AppConfig(), new SearchEngine(new UsageDb()), new IconCache(),
            (e, ra) => { }, () => { }, files => { });
        Win32.GetClientRect(mw.Hwnd, out var mrc);
        Check(mrc.Width == mw.LayoutWidth && mrc.Height == mw.LayoutHeight,
            "主窗口客户区与 DPI 布局一致", $"client={mrc.Width}x{mrc.Height} layout={mw.LayoutWidth}x{mw.LayoutHeight}");
        Win32.DestroyWindow(mw.Hwnd);
    }
}
