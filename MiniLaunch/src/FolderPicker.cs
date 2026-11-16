using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 文件夹选择对话框（现代「资源管理器风格」：shell 的 IFileDialog + FOS_PICKFOLDERS）
//
// 为什么不用 SHBrowseForFolder：那是 Win9x 时代的「小树形窗」，跟资源管理器完全不像。
// 现代选择器与「打开文件」窗口同源：左侧导航树 + 右侧文件列表 + 地址栏 +
// 底部「文件夹:」输入框 + 「选择文件夹 / 取消」，还能直接新建文件夹。
//
// 坑 1（#28）：shell 对话框要求调用线程是 **STA**。.NET 主线程默认是 COM MTA，
// MTA 下对话框会被创建却**始终不显示**（WS_VISIBLE 没置上）。所以这里统一放到
// 自建的 STA 线程执行，调用方线程在等待期间继续泵消息，UI 不会卡死。
//
// 坑 2（#34，取代 #30 的 ComImport 写法）：本项目发布配置是「单文件 + 裁剪」，ILLink 会
//   a) 默认关掉内置 COM 封送（BuiltInComInteropSupport=false）→ Marshal.GetObjectForIUnknown
//      直接抛 NotSupported_COM：发布版「添加文件夹…」一直在静默退化成旧树形窗，
//      直到自检的槽位探测在发布版稳定 FAIL 才暴露（Debug/JIT 版全绿，具隐蔽性）；
//   b) 即使显式保留 COM 支持，ComImport 接口里**没被调用的方法**也会被裁剪压缩，
//      vtable 槽位整体错位 → 调 SetOptions 实际打到了 Unadvise 上，实测 AccessViolation。
//   因此 COM 调用全部改为**手工 vtable**（GetDelegateForFunctionPointer），不经过 RCW，
//   与裁剪配置完全解耦。槽位顺序（IUnknown 占 0~2，本机 Win11 逐槽实测确认）：
//     IFileOpenDialog：3 Show / 4 SetFileTypes / 5 SetFileTypeIndex / 6 GetFileTypeIndex /
//       7 Advise / 8 Unadvise / 9 SetOptions / 10 GetOptions / 11 SetFolder /
//       12 SetDefaultFolder / 13 GetFolder / 14 GetCurrentSelection / 15 SetFileName /
//       16 GetFileName / 17 SetFileNameLabel / 18 SetDefaultExtension / 19 Close / 20 GetResult
//     （微软文档里的方法列表是按字母排的，不是 vtable 顺序！）
//     IShellItem：3 BindToHandler / 4 GetParent / 5 GetDisplayName / 6 GetAttributes / 7 Compare
//
// 坑 3：CoCreateInstance / GetResult 交回来的 COM 指针是**我们的引用**，用完必须 Marshal.Release。
public static class FolderPicker
{
    // ===== FILEOPENDIALOGOPTIONS =====
    // 注意：FOS_PATHMUSTEXIST(0x400) 与 FOS_PICKFOLDERS 同时使用会被 shell 判为非法
    // （SetOptions 返回 E_INVALIDARG），现代对话框本来就只让选已存在的项，故不使用。
    const uint FOS_NOCHANGEDIR = 0x00000008;
    const uint FOS_PICKFOLDERS = 0x00000020;
    const uint FOS_FORCEFILESYSTEM = 0x00000040;
    const uint FOS_FILEMUSTEXIST = 0x00000800;

    /// <summary>SIGDN_FILESYSPATH：取 item 的文件系统路径（PWSTR，CoTaskMemFree 释放）。</summary>
    const uint SIGDN_FILESYSPATH = 0x80058000;

    // CLSID_FileOpenDialog（comdlg32.dll 里的「File Open Dialog」）/ IID_IFileOpenDialog
    static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    static readonly Guid IidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    // ===== 手工 vtable 槽位 =====
    const int SlotShow = 3, SlotSetOptions = 9, SlotGetOptions = 10, SlotGetResult = 20;
    const int SlotGetDisplayName = 5;   // IShellItem

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ShowFn(IntPtr self, IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetOptionsFn(IntPtr self, uint fos);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetOptionsFn(IntPtr self, out uint fos);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetResultFn(IntPtr self, out IntPtr ppsi);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDisplayNameFn(IntPtr self, uint sigdnName, out IntPtr ppszName);

    /// <summary>取 COM 对象 vtable 第 slot 槽的函数指针（slot 含 IUnknown 的 3 槽偏移）。</summary>
    static T Vtable<T>(IntPtr ppv, int slot) where T : class =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(ppv), slot * IntPtr.Size));

    static void ReleaseCom(IntPtr p)
    {
        if (p != IntPtr.Zero) { try { Marshal.Release(p); } catch { } }
    }

    // ===== 选文件夹 / 选文件共用同一套（现代 IFileOpenDialog + STA 线程）=====

    /// <summary>弹出文件夹选择对话框；返回选中目录的绝对路径，取消返回 null。</summary>
    public static string? Pick(IntPtr owner, string title) =>
        RunOnStaThread(() => PickCore(owner, title, PickKind.Folder));

    /// <summary>
    /// 选择文件（托盘「添加程序…」）。以前用 comdlg32 的 GetOpenFileNameW：
    /// ① 是 Win95 时代的老对话框，与「选文件夹」的现代风格不一致；
    /// ② 在 MTA 主线程上同步调用，与坑 #28 的结论（shell 对话框必须在 STA 线程）相背。
    /// 现在复用选文件夹的同一套实现，只把 FILEOPENDIALOGOPTIONS 换成「选文件」。
    /// </summary>
    public static string? PickFile(IntPtr owner, string title) =>
        RunOnStaThread(() => PickCore(owner, title, PickKind.File));

    enum PickKind { Folder, File }

    /// <summary>把阻塞式 shell 对话框放到 STA 线程上跑，本线程泵消息等待。</summary>
    static string? RunOnStaThread(Func<string?> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return work();

        string? result = null;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { err = ex; }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.ShellPicker",
        };
        t.SetApartmentState(ApartmentState.STA);   // 必须在启动线程前设置
        t.Start();

        // 等待期间继续泵消息：对话框在另一个线程上跑模态循环，本线程不能停摆。
        // 关键：WM_QUIT 必须**留在队列里**（先用 PM_NOREMOVE 看一眼，见到就收手）。
        // 一旦用 PM_REMOVE 把它摘掉，DispatchMessage(WM_QUIT) 什么也不做 → 退出信号丢失，
        // 外层 GetMessageW 永远阻塞：窗口已销毁却仍占着单实例 Mutex，
        // 用户之后双击 exe 完全没反应，只能去任务管理器杀进程。
        while (t.IsAlive)
        {
            while (Win32.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE))
            {
                if (msg.message == Win32.WM_QUIT) return null;   // 退出信号交给外层 RunLoop
                if (!Win32.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE)) break;
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
        if (err != null) return null;
        return result;
    }

    /// <summary>在 STA 线程上执行真正的对话框调用。</summary>
    static string? PickCore(IntPtr owner, string title, PickKind kind)
    {
        // 本线程必须先初始化成 STA，否则 shell 对话框会创建却不显示
        bool comInit = false;
        try { comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0; }
        catch { comInit = false; }
        try
        {
            // 优先用现代对话框；系统不支持（极老的 Windows）时退回旧版对话框
            if (TryCreateModernDialog(kind, out IntPtr dlg))
                return RunModernDialog(dlg, owner);
            return PickLegacy(owner, title, kind);
        }
        finally
        {
            if (comInit) { try { Win32.CoUninitialize(); } catch { } }
        }
    }

    /// <summary>创建现代对话框对象（CLSID_FileOpenDialog）。成功时返回裸指针，调用方负责 Release。</summary>
    static bool TryCreateModernDialog(PickKind kind, out IntPtr dlg)
    {
        dlg = IntPtr.Zero;
        try
        {
            Guid clsid = ClsidFileOpenDialog;
            Guid iid = IidFileOpenDialog;
            int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero,
                Win32.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);
            if (hr < 0 || ppv == IntPtr.Zero) return false;
            dlg = ppv;

            // 选项必须能被 shell 接受，否则会退化成错误模式
            if (Vtable<GetOptionsFn>(dlg, SlotGetOptions)(dlg, out uint opts) < 0)
            {
                ReleaseCom(dlg); dlg = IntPtr.Zero;
                return false;
            }
            opts |= FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_NOCHANGEDIR;
            if (kind == PickKind.Folder) opts |= FOS_PICKFOLDERS;
            if (Vtable<SetOptionsFn>(dlg, SlotSetOptions)(dlg, opts) < 0)
            {
                ReleaseCom(dlg); dlg = IntPtr.Zero;
                return false;
            }
            return true;
        }
        catch
        {
            ReleaseCom(dlg); dlg = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>显示现代对话框并取回选中的目录。返回 null = 用户取消。总是释放 dlg。</summary>
    static string? RunModernDialog(IntPtr dlg, IntPtr owner)
    {
        try
        {
            int hrShow = Vtable<ShowFn>(dlg, SlotShow)(dlg, owner);
            if (hrShow < 0) return null;   // 用户取消（0x800704C7）

            int hrGet = Vtable<GetResultFn>(dlg, SlotGetResult)(dlg, out IntPtr pItem);
            if (hrGet < 0 || pItem == IntPtr.Zero) return null;
            try
            {
                // GetResult 交回来的就是 IShellItem*（我们持有引用，finally 里 Release）
                int hrName = Vtable<GetDisplayNameFn>(pItem, SlotGetDisplayName)(pItem, SIGDN_FILESYSPATH, out IntPtr pName);
                if (hrName < 0 || pName == IntPtr.Zero) return null;
                try
                {
                    string path = Marshal.PtrToStringUni(pName) ?? "";
                    return path.Length == 0 ? null : path.Trim();
                }
                finally { Win32.CoTaskMemFree(pName); }
            }
            finally
            {
                ReleaseCom(pItem);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(dlg);
        }
    }

    /// <summary>旧版对话框——仅作为现代对话框不可用时的兜底（选目录=树形窗，选文件=打开文件窗）。</summary>
    static string? PickLegacy(IntPtr owner, string title, PickKind kind)
    {
        if (kind == PickKind.File) return PickFileLegacy(owner, title);

        IntPtr dispName = IntPtr.Zero;
        try
        {
            // pszDisplayName 由调用方分配（Unicode）：对话框会把用户选中的目录名写回来
            dispName = Marshal.StringToHGlobalUni(new string('\0', 260));
            var bi = new Win32.BROWSEINFO
            {
                hwndOwner = owner,
                pszDisplayName = dispName,
                lpszTitle = title,
                ulFlags = Win32.BIF_RETURNONLYFSDIRS | Win32.BIF_NEWDIALOGSTYLE | Win32.BIF_EDITBOX,
            };
            IntPtr pidl = Win32.SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;   // 用户取消
            try
            {
                var sb = new StringBuilder(260);
                if (!Win32.SHGetPathFromIDListW(pidl, sb, sb.Capacity)) return null;
                string path = sb.ToString().Trim();
                return path.Length == 0 ? null : path;
            }
            finally
            {
                Win32.CoTaskMemFree(pidl);   // PIDL 是 shell 分配的 COM 内存，必须由我们释放
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dispName != IntPtr.Zero) Marshal.FreeHGlobal(dispName);
        }
    }

    /// <summary>旧版选文件对话框（GetOpenFileNameW），仅供现代对话框不可用的老系统兑底。</summary>
    static string? PickFileLegacy(IntPtr owner, string title)
    {
        const int BufChars = 1024;
        IntPtr buf = Marshal.AllocHGlobal(BufChars * 2);
        try
        {
            // 清零：shell 会把这块缓冲当 C 字符串读，脏数据会导致随机文件名
            Marshal.Copy(new byte[BufChars * 2], 0, buf, BufChars * 2);
            var ofn = new Win32.OPENFILENAMEW
            {
                lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAMEW>(),
                hwndOwner = owner,
                lpstrFilter = "程序\0*.exe;*.lnk;*.url\0所有文件\0*.*\0",
                lpstrFile = buf,
                nMaxFile = BufChars,
                lpstrTitle = title,
                Flags = Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_HIDEREADONLY,
            };
            if (!Win32.GetOpenFileNameW(ref ofn)) return null;
            string? f = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(f) ? null : f;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>自检用：确认系统能创建现代对话框（IFileDialog 可用）。fileMode=true 时验「选文件」模式。</summary>
    public static bool CanCreateModernDialog(bool fileMode = false)
    {
        bool ok = false;
        LastCreateHr = unchecked((int)0x80004005);
        LastSetOptionsHr = -1;
        var t = new Thread(() =>
        {
            IntPtr ppv = IntPtr.Zero;
            try
            {
                bool comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0;
                try
                {
                    Guid clsid = ClsidFileOpenDialog;
                    Guid iid = IidFileOpenDialog;
                    int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero,
                        Win32.CLSCTX_INPROC_SERVER, ref iid, out ppv);
                    LastCreateHr = hr;
                    ok = hr >= 0 && ppv != IntPtr.Zero;
                    if (ok)
                    {
                        // 再验证一次：选项必须能被 shell 接受（否则对话框会退化成文件选择）
                        int hrOpt = Vtable<GetOptionsFn>(ppv, SlotGetOptions)(ppv, out uint opts);
                        opts |= FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_NOCHANGEDIR;
                        if (!fileMode) opts |= FOS_PICKFOLDERS;
                        if (hrOpt >= 0)
                            hrOpt = Vtable<SetOptionsFn>(ppv, SlotSetOptions)(ppv, opts);
                        LastSetOptionsHr = hrOpt;
                        if (hrOpt < 0) ok = false;
                    }
                }
                finally
                {
                    ReleaseCom(ppv);
                    if (comInit) { try { Win32.CoUninitialize(); } catch { } }
                }
            }
            catch { }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.FolderPicker.SelfTest",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(5000);
        return ok;
    }

    /// <summary>最近一次 CoCreateInstance 的 HRESULT（0x80004005 = 未尝试/失败）。</summary>
    public static int LastCreateHr { get; private set; }

    /// <summary>最近一次 SetOptions 的 HRESULT（负数表示选项被 shell 拒绝）。</summary>
    public static int LastSetOptionsHr { get; private set; } = -1;

    /// <summary>
    /// 自检用：验证 IFileOpenDialog 的 GetResult 槽位没有错位。
    /// 正确槽位（20）在对话框尚未显示时返回 E_UNEXPECTED(0x8000FFFF)；
    /// 一旦错位到别的槽（19/21/...）返回的 HRESULT 就不一样（实测：19→S_OK、16→E_FAIL）。
    /// </summary>
    public static bool CanProbeResultSlot()
    {
        bool ok = false;
        LastProbeError = "未运行";
        var t = new Thread(() =>
        {
            IntPtr ppv = IntPtr.Zero;
            try
            {
                bool comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0;
                try
                {
                    Guid clsid = ClsidFileOpenDialog;
                    Guid iid = IidFileOpenDialog;
                    int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero,
                        Win32.CLSCTX_INPROC_SERVER, ref iid, out ppv);
                    if (hr < 0 || ppv == IntPtr.Zero)
                    {
                        LastProbeError = $"CoCreateInstance 失败 hr=0x{hr:X8}";
                        return;
                    }
                    LastProbeError = "已创建，GetResult 调用中";
                    int hrGet = Vtable<GetResultFn>(ppv, SlotGetResult)(ppv, out IntPtr pItem);
                    ReleaseCom(pItem);
                    LastResultSlotHr = hrGet;
                    LastProbeError = "";
                    ok = hrGet == unchecked((int)0x8000FFFF);   // E_UNEXPECTED
                }
                finally
                {
                    ReleaseCom(ppv);
                    if (comInit) { try { Win32.CoUninitialize(); } catch { } }
                }
            }
            catch (Exception ex) { LastProbeError = ex.GetType().Name + ": " + ex.Message; }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.FolderPicker.SelfTest",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(8000);
        return ok;
    }

    /// <summary>最近一次 GetResult 探测的 HRESULT（诊断用）。</summary>
    public static int LastResultSlotHr { get; private set; }

    /// <summary>最近一次探测失败的原因（诊断用：区分「线程没跑」与「哪一步 COM 调用抛了」）。</summary>
    public static string LastProbeError { get; private set; } = "未运行";
}
