# MiniLaunch 极简启动器

Windows 桌面启动器，对标 uTools，只做三件事：**呼出 → 搜索 → 启动**。

- 纯 Win32 P/Invoke 自绘 UI，**不依赖任何 UI 框架**（无 WinForms / WPF / WebView / Electron）
- **零 NuGet 包、零反射**（JSON 走 `System.Text.Json` 源生成）
- 常驻内存极小：隐藏状态 ≤ 60MB，NativeAOT 后 ≤ 30MB
- 三路来源合并：自定义条目 > 开始菜单 > 注册表卸载项 > UWP 商店应用

## 功能

| 功能 | 说明 |
| --- | --- |
| 全局热键呼出 | 默认 `Ctrl+Space`，可托盘现场录制修改 |
| 模糊搜索 | 分词 AND 匹配 + 打分（名前缀 > 名包含 > 别名 > 路径），24h 内用过的条目加权 |
| 拼音别名 | 内置常见中文软件别名表（`wx` → 微信），不引全量拼音库 |
| 图标缓存 | 后台线程提取图标，UI 绘制只读缓存，LRU 64 项 HICON |
| 拖放启动 | 把 exe/快捷方式拖进窗口即添加为自定义条目 |
| 托盘菜单 | 设置热键 / 添加程序 / 重新扫描 / 开机自启 / 退出 |

## 环境要求

- Windows 10 1607+ / Windows 11（不支持 Win7 / 8.1，.NET 8 运行时本身不支持）
- 运行时**不需要安装 .NET**：发布产物为自包含单文件 exe（约 12MB）

## 构建

```bash
cd MiniLaunch && dotnet build
```

日常调试用 Debug（JIT）即可，本机无 MSVC。

## 自检

```bash
MiniLaunch/bin/Debug/net8.0-windows/MiniLaunch.exe --selftest
```

必须全绿（当前基线 **45 项通过**），退出码 0。改动后必跑。

## 发布单文件

> `-o` 必须给**纯 ASCII 绝对路径**（仓库目录名含中文，路径解析会错位）。

```bash
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true \
  -o "E:\code\_tmp_publish"
cp /e/code/_tmp_publish/MiniLaunch.exe /e/code/_tmp_publish/MiniLaunch.pdb \
   "/e/code/启动器/publish/win-x64/"
```

发布后核对：`./MiniLaunch.exe --selftest` 应显示 45 项通过。

## 目录结构

```
MiniLaunch/src/
  Program.cs     入口：单实例、热键、托盘、消息循环、两阶段扫描
  Win32.cs       全部 P/Invoke / 结构体 / 常量
  Config.cs      配置与 JSON 源生成
  Entry.cs       条目模型与合并去重
  Scanners.cs    开始菜单 / 注册表 / UWP 扫描
  Pinyin.cs      中文软件别名表
  Search.cs      分词匹配与打分
  IconCache.cs   HICON 提取与 LRU 缓存
  AppIcon.cs     应用图标加载
  Dpi.cs         DPI 感知设置与降级
  Launcher.cs    ShellExecuteEx 封装
  Theme.cs       配色与 GDI 绘制工具
  MainWindow.cs  主窗口（无边框自绘）
  HotkeyDialog.cs 热键录制
  Tray.cs        托盘菜单
  SelfTest.cs    自检断言
```

设计依据与已知问题处置见 [`方案.md`](方案.md)、[`详细计划.md`](详细计划.md)、[`审查记录.md`](审查记录.md)、
[`AGENTS.md`](AGENTS.md)（改动前请先读）。

## 数据位置

`%APPDATA%\MiniLaunch\`：`config.json`（热键/自启/自定义条目）、`usage.json`（使用统计）、`uwp-cache.json`（UWP 缓存）。
