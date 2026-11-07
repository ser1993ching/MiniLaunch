# GitHub 仓库 About 设置 / GitHub Repo About Settings

在仓库主页右侧栏（或 `Settings → General`）设置。下面的内容已按 GitHub 字段长度限制校验过。

Use the repo sidebar's **About** gear (or `Settings → General`). The values below are pre-validated against GitHub's field limits.

---

## Description（描述，上限 350 字符，当前 145）

> 极简 Windows 启动器：呼出→搜索→启动。纯 Win32 原生自绘，零 NuGet 依赖、零反射。空闲 CPU 0.000%、私有内存仅 15MB、长期挂机内存零增长——低内存、低资源占用。Minimal Win32 launcher: 0% idle CPU, ~15MB RAM.

**设计要点 / Rationale**

- 第一句说明它是什么（对标 uTools 的极简启动器）
- 中段说明技术底座（纯 Win32 自绘、零 NuGet、零反射）
- **核心卖点：低内存、低资源占用**，附实测数字（0.000% 空闲 CPU、15MB 私有内存、零增长）
- 末尾附英文短句，便于国际访客一眼读懂

---

## Topics（最多 20 个，当前 20 个）

```
launcher,win32,low-memory,memory-efficient,lightweight,performance,csharp,dotnet8,native-ui,no-nuget,no-dependencies,single-file,pinvoke,gdi,windows,hotkey,global-hotkey,fuzzy-search,utools-alternative,start-menu
```

**分类 / Categories**

| 类别 | Topics |
| --- | --- |
| 定位 | `launcher` `utools-alternative` `start-menu` `hotkey` `global-hotkey` `fuzzy-search` |
| 技术栈 | `win32` `csharp` `dotnet8` `native-ui` `pinvoke` `gdi` `windows` |
| **低内存卖点** | `low-memory` `memory-efficient` `lightweight` `performance` |
| 轻量化交付 | `no-nuget` `no-dependencies` `single-file` |

> Topics 在 GitHub 搜索里是重要的发现入口，`low-memory` / `memory-efficient` / `lightweight` 这几个正是本项目的核心差异化关键词。

---

## 相关链接 / Related links

- 仓库首页：<https://github.com/ser1993ching/MiniLaunch>
- 设置页：<https://github.com/ser1993ching/MiniLaunch/settings>
- 顶部横幅（About 右侧的 **Customize** 可添加）：
  - 徽章 `https://img.shields.io/badge/memory-15MB-brightgreen` （内存）
  - 徽章 `https://img.shields.io/badge/idle_CPU-0.000%25-brightgreen` （空闲 CPU）
  - 徽章 `https://img.shields.io/badge/deps-0_NuGet-brightgreen` （零依赖）
