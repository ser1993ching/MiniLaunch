using Microsoft.Win32;

namespace MiniLaunch;

/// <summary>
/// 开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 项的读取、写入、校验与自愈。
///
/// 设计要点（针对「设了开机自启，但重启后并没有启动」这个历史问题）：
///   1. 写入后必须回读校验 —— 注册表写入被策略/安全软件拦截时**不会抛异常**，只是不生效；
///   2. 失败必须回滚 UI 状态 —— 不能出现「菜单显示已勾选、注册表里却什么都没有」；
///   3. 每次启动做一次自愈 —— 注册表项指向的文件没了（构建目录被清理/搬走）就重新写回；
///   4. 勾选状态以注册表为准（真相源），config.json 只是用户意图的备份；
///   5. 自启目标应当是**稳定路径**（%LOCALAPPDATA%\MiniLaunch），而不是 bin\Debug 这类临时构建目录。
/// </summary>
public static class AutoStart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MiniLaunch";
    public const string TaskName = "MiniLaunch";

    /// <summary>稳定安装目录：%LOCALAPPDATA%\MiniLaunch（自启指向这里，删构建目录也不会失效）。</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniLaunch");

    public static string InstalledExe => Path.Combine(InstallDir, "MiniLaunch.exe");

    // ================= 读取 =================

    /// <summary>注册表里的原始命令行（可能带引号、可能带参数）。</summary>
    public static string? ReadRawCommand(string? valueName = null)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(string.IsNullOrEmpty(valueName) ? ValueName : valueName) as string;
        }
        catch { return null; }
    }

    /// <summary>从命令行里解析出 exe 路径（去引号、去参数）。</summary>
    public static string? ParseExeFromCommand(string? rawCommand)
    {
        string raw = rawCommand?.Trim() ?? "";
        if (raw.Length == 0) return null;
        if (raw[0] == '"')
        {
            int end = raw.IndexOf('"', 1);
            return end > 1 ? raw.Substring(1, end - 1) : null;
        }
        int sp = raw.IndexOf(' ');
        string p = sp > 0 ? raw.Substring(0, sp) : raw;
        return p.Length > 0 ? p : null;
    }

    /// <summary>从注册表命令行里解析出 exe 路径（去引号、去参数）。</summary>
    public static string? ReadTargetExe() => ParseExeFromCommand(ReadRawCommand());

    /// <summary>自启是否处于「真的能用」的状态：注册表有项，且目标文件确实存在。</summary>
    public static bool IsActive()
    {
        string? exe = ReadTargetExe();
        return !string.IsNullOrEmpty(exe) && File.Exists(exe);
    }

    // ================= 写入 =================

    /// <summary>写入 Run 项并回读校验。false = 没写进去（调用方必须回滚 UI 状态并提示）。</summary>
    public static bool TryEnable(string exePath, string? valueName = null)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        string name = string.IsNullOrEmpty(valueName) ? ValueName : valueName;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k == null) return false;
            k.SetValue(name, Quote(exePath), RegistryValueKind.String);
            string? back = k.GetValue(name) as string;
            return back != null && SameExe(back, exePath);
        }
        catch { return false; }
    }

    /// <summary>删除 Run 项并回读校验。</summary>
    public static bool TryDisable(string? valueName = null)
    {
        string name = string.IsNullOrEmpty(valueName) ? ValueName : valueName;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k == null) return false;
            k.DeleteValue(name, throwOnMissingValue: false);
            return k.GetValue(name) == null;
        }
        catch { return false; }
    }

    public static string Quote(string p) => "\"" + p + "\"";

    public static string Strip(string s) => s.Trim().Trim('"');

    /// <summary>两个命令行/路径是否指向同一个 exe（忽略大小写与首尾引号）。</summary>
    public static bool SameExe(string a, string b) =>
        string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>路径是否位于构建输出目录（bin\Debug、bin\Release、obj）——这类路径不适合做自启目标。</summary>
    public static bool IsBuildOutput(string path)
    {
        string p = path.Replace('/', '\\');
        return p.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    }

    // ================= 启动自愈 =================

    public enum HealResult
    {
        /// <summary>一切正常，无需改动。</summary>
        Ok,
        /// <summary>注册表项已失效，已重新写回。</summary>
        Repaired,
        /// <summary>用户已关闭自启，残留的注册表项已清理。</summary>
        Disabled,
        /// <summary>首次运行，注册表里已有可用项，已采纳为用户意图。</summary>
        Adopted,
        /// <summary>修不了（当前跑的是构建目录里的临时产物，或写入被拒）。</summary>
        Failed,
        /// <summary>无需处理。</summary>
        Skipped,
    }

    /// <summary>最近一次自愈的详细过程（诊断用，可直接写进报告文件）。</summary>
    public static string LastHealDetail { get; private set; } = "";

    /// <summary>
    /// 启动时自愈：让注册表与「用户意图 + 当前 exe」保持一致。
    /// </summary>
    /// <param name="wantEnabled">config.json 里的用户意图</param>
    /// <param name="currentExe">当前运行的 exe 路径</param>
    /// <param name="configExists">config.json 是否存在（首次运行为 false）</param>
    public static HealResult Heal(bool wantEnabled, string? currentExe, bool configExists)
    {
        string? raw = ReadRawCommand();
        string? target = ReadTargetExe();
        bool regHas = !string.IsNullOrEmpty(raw);
        bool targetOk = regHas && target != null && File.Exists(target);
        var log = new List<string>
        {
            "注册表值   : " + (string.IsNullOrEmpty(raw) ? "(未设置)" : raw),
            "解析目标   : " + (target ?? "(无)"),
            "目标存在   : " + (targetOk ? "是" : "否"),
            "用户意图   : " + (wantEnabled ? "开启" : "关闭"),
            "config存在 : " + (configExists ? "是" : "否（首次运行）"),
            "当前 exe   : " + (currentExe ?? "(未知)"),
        };
        HealResult result;

        if (!configExists)
        {
            // 首次运行：注册表里已有可用项 → 采纳为用户意图（只回报，不写注册表）
            result = targetOk ? HealResult.Adopted : HealResult.Skipped;
            LastHealDetail = string.Join("\n", log) + "\n结论       : " + result;
            return result;
        }

        if (!wantEnabled)
        {
            // 用户明确关闭过自启：注册表里还有残留就清掉（保持 UI 与现实一致）
            if (!regHas) { result = HealResult.Ok; }
            else { result = TryDisable() ? HealResult.Disabled : HealResult.Failed; }
            LastHealDetail = string.Join("\n", log) + "\n结论       : " + result;
            return result;
        }

        // 用户要自启
        if (targetOk)
        {
            // 已指向一个存在的 exe。若指向的是另一个副本（例如 %LOCALAPPDATA% 里的正式版，
            // 而当前跑的是调试副本），**不要**改写——用户日常用的正是那个正式版。
            result = HealResult.Ok;
            LastHealDetail = string.Join("\n", log) + "\n结论       : " + result + "（已指向有效文件，不改写）";
            return result;
        }

        // 注册表项缺失，或目标文件已经不在了 → 重新写回
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            result = HealResult.Failed;
            LastHealDetail = string.Join("\n", log) + "\n结论       : 失败（当前 exe 不存在）";
            return result;
        }
        if (IsBuildOutput(currentExe))
        {
            // 当前是构建目录里的临时产物，写进去也会随构建目录消失而再次失效 → 不写
            result = HealResult.Failed;
            LastHealDetail = string.Join("\n", log) + "\n结论       : 失败（当前是构建目录，指向它会再次失效）";
            return result;
        }
        bool wrote = TryEnable(currentExe);
        result = wrote ? HealResult.Repaired : HealResult.Failed;
        log.Add("写入结果   : " + (wrote ? "成功" : "失败（被拒绝或被拦截）"));
        LastHealDetail = string.Join("\n", log) + "\n结论       : " + result;
        return result;
    }

    // ================= 安装到本地 =================

    /// <summary>
    /// 把当前程序复制到 %LOCALAPPDATA%\MiniLaunch（稳定路径），供开机自启指向。
    /// 框架依赖的构建还会一并复制 .dll / runtimeconfig / deps。
    /// </summary>
    /// <param name="sourceExe">源 exe 路径</param>
    /// <param name="targetDir">目标目录（默认 %LOCALAPPDATA%\MiniLaunch）；自检可传临时目录避免副作用。</param>
    public static (bool ok, string msg) InstallLocal(string sourceExe, string? targetDir = null)
    {
        if (string.IsNullOrEmpty(sourceExe) || !File.Exists(sourceExe))
            return (false, "找不到当前程序文件。");
        string dir = string.IsNullOrEmpty(targetDir) ? InstallDir : targetDir;
        string dest = Path.Combine(dir, "MiniLaunch.exe");
        if (SameExe(sourceExe, dest)) return (true, dest);
        try
        {
            Directory.CreateDirectory(dir);
            string srcDir = Path.GetDirectoryName(sourceExe) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(sourceExe);

            File.Copy(sourceExe, dest, true);
            foreach (string extra in new[] { baseName + ".dll", baseName + ".runtimeconfig.json", baseName + ".deps.json" })
            {
                string src = Path.Combine(srcDir, extra);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, extra), true);
            }
            return (true, dest);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ================= 任务计划程序（可选的另一种自启方式） =================

    /// <summary>执行 schtasks 并返回退出码（0 = 成功）。只取退出码，不读输出，避免代码页解码问题。</summary>
    static int RunSchtasks(string[] args, int timeoutMs)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = new System.Diagnostics.Process { StartInfo = psi };
            p.Start();
            // 两条管道必须并行读（坑 #18 同款）：顺序 ReadToEnd 在 stderr 写满时死锁，
            // 且同步读会让 WaitForExit 超时失效。输出内容不需要，只要退出码。
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>是否已注册任务计划（用于排查「安全软件拦了 Run 项」的场景）。</summary>
    public static bool IsTaskRegistered() =>
        RunSchtasks(new[] { "/Query", "/TN", TaskName }, 8000) == 0;

    /// <summary>
    /// 注册「登录即启动」的任务计划（不需要管理员）。
    /// 任务计划走的是任务调度器，Run 项被安全软件拦截时可以改用它。
    /// 注意：真正的「系统启动即运行（未登录）」需要管理员权限创建 ONSTART 触发器。
    /// </summary>
    public static bool RegisterLogonTask(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        return RunSchtasks(new[]
        {
            "/Create", "/TN", TaskName,
            "/TR", Quote(exePath),
            "/SC", "ONLOGON", "/F",
        }, 10000) == 0;
    }

    /// <summary>注销任务计划。</summary>
    public static bool UnregisterTask() =>
        RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" }, 8000) == 0;
}
