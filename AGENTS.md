# AGENTS.md — MiniLaunch 极简启动器

Windows 桌面启动器（对标 uTools，只做「呼出 → 搜索 → 启动」）。**核心非功能需求是常驻内存极小**（隐藏状态 ≤ 60MB，AOT 后 ≤ 30MB），任何改动不得破坏这一点。

## 技术栈与硬性约束（违反即回退）

- C# / .NET 8 / `net8.0-windows`，**纯 Win32 P/Invoke 自绘 UI**。
- 禁止引入：WinForms、WPF、WebView、Electron、System.Drawing、任何 NuGet 包。
- 禁止反射：JSON 必须走源生成（`JsonCtx`）；新增可序列化类型需在 `JsonCtx` 加 `[JsonSerializable]`。
- 所有 Win32 API 用 **W 版本**（中文路径必须可用）。
- 源文件必须 **UTF-8 with BOM**（系统代码页为 GBK 时会乱码；csproj 已设 `CodePage=65001`，BOM 是双保险）。新建/重写任何 `.cs` 都要带 BOM。
- 常驻内存来源：窗口隐藏用 `SW_HIDE`（不销毁）、零定时器、零轮询、图标只保留 LRU 64 项 HICON。

## 常用命令

```bash
# 构建（Debug，JIT；本机无 MSVC，日常调试用这个）
cd MiniLaunch && dotnet build

# 无头自检（必须全绿，退出码 0）——改代码后必跑
MiniLaunch/bin/Debug/net8.0-windows/MiniLaunch.exe --selftest
# 报告：会产生 selftest-report.txt（结论同时写 stdout 管道）

# 发布单文件（自包含 + 裁剪 + R2R，15MB 左右）
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true \
  -o ../../publish/win-x64
```

发布时允许的唯一已知告警：`IL2008 System.Diagnostics.FileVersionInfo ... System.SR`（无害）。若新增裁剪告警，必须逐条核对是否因引入反射。
NativeAOT（`publish-aot.cmd`）**尚未创建**，需先装 VC 工具链。

## 目录

```
MiniLaunch/src/
  Program.cs     入口：--selftest 分发、单实例 Mutex + 命名 Event、热键、托盘、消息循环、后台扫描
  Win32.cs       全部 P/Invoke / 结构体 / 常量（新增 API 只加在这里）
  Config.cs      AppConfig / CustomEntry / UsageDb / JsonCtx 源生成 / Paths / ConfigStore 原子写 / HotkeyParser
  Entry.cs       Entry 模型 + EntryMerger（优先级：Custom > StartMenu > Registry > Uwp，按小写名去重）
  Scanners.cs    StartMenuScanner（递归 .lnk/.url）/ RegistryScanner（3 个 Uninstall 键 + 噪声过滤）/ UwpScanner（powershell Get-StartApps → JSON，7 天缓存）
  Pinyin.cs      常见中文软件别名表（wx→微信），不引全量拼音表
  Search.cs      分词 AND 匹配 + 打分（名前缀 100 > 名包含 80+位置 > 别名 60 > 路径 30；+log2(1+Count)*12；24h 内 +15）
  IconCache.cs   HICON 提取（SHGetFileInfo / ExtractIconEx）+ LRU(64)，驱逐时 DestroyIcon
  Launcher.cs    ShellExecuteEx 封装（runas / 工作目录 / explorer /select）
  MainWindow.cs  无边框 WS_POPUP + 原生 EDIT 子类化 + 自绘双缓冲列表 + 拖放 + DPI + 右键菜单 + 失焦隐藏
  Tray.cs        托盘菜单（添加程序 / 重新扫描 / 开机自启 / 退出）
  NativeOut.cs   管道 stdout 输出（WinExe 无控制台）
  SelfTest.cs    --selftest 全部断言
方案.md / 详细计划.md / 审查记录.md   设计依据与已知问题处置（改架构前先读）
```

## 运行时数据

`%APPDATA%\MiniLaunch\`：`config.json`（热键/自启/自定义条目）、`usage.json`（使用次数与时间）、`uwp-cache.json`。
写入一律走 `ConfigStore.SaveAtomic`（写 `.tmp` 再替换）。改配置格式要兼容旧文件（缺字段用默认值，`LoadConfig` 已有 try-catch 兜底）。

## 易踩的坑（已在实现中处理，勿改回去）

1. **失焦隐藏**：只在「前台窗口线程 ≠ 本线程」时隐藏，否则右键菜单/文件对话框会导致误隐藏。
2. **TrackPopupMenu**：菜单前 `SetForegroundWindow`，菜单后 `PostMessage(WM_NULL)`，否则点外部关不掉。
3. **IME**：EDIT 需子类化（comctl32 `SetWindowSubclass`），`VK_PROCESSKEY` 必须直通给输入法。
4. **热键**：`RegisterHotKey` 失败要在托盘气泡提示并允许改 `config.json`；抢前台失败用 `AttachThreadInput` 兜底。
5. **扫描线程**结束要 `PostMessage(WM_APP_SCAN_DONE)` 刷新列表，不能直接碰 UI。
6. **图标提取不在 UI 线程同步做重活**：IconRef 指向网络盘时 WM_PAINT 会卡。
7. **UWP**：`LaunchPath=explorer.exe` + `Args=shell:AppsFolder\<AUMID>`；powershell 输出前置 `[Console]::OutputEncoding=[Text.Encoding]::UTF8`。
8. **用量落盘**：每次启动成功立即写 + 进程退出 + `WM_QUERYENDSESSION`。

## 工作方式约定

- 先读 `方案.md` / `详细计划.md` / `审查记录.md` 再动结构性代码；功能裁剪清单在 `方案.md` 第八节（插件/剪贴板/截图/同步等**明确不做**）。
- 改动完成的标准：`dotnet build` 0 警告 0 错误 + `--selftest` 全绿（当前基线 26 项通过）。新增能力时同步加断言到 `SelfTest.cs`。
- 注释与 UI 文案用中文，风格与现有文件一致（`file-scoped namespace`、`static` 工具类、紧凑写法）。
- 无 git 仓库；不要执行 `git` 相关操作。
- 本机 bash 输出可能被 rtk 钩子压缩（行被折叠成裸花括号），需要看完整源码请用 read 工具而非 cat/head。
