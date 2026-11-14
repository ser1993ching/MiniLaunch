using System.Runtime.InteropServices;
using System.Text;

namespace MiniLaunch;

// 文件夹选择对话框（现代「资源管理器风格」：shell 的 IFileDialog + FOS_PICKFOLDERS）
//
// 为什么不用 SHBrowseForFolder：那是 Win9x 时代的「小树形窗」，跟资源管理器完全不像。
// 现代选择器与「打开文件」窗口同源：左侧导航树 + 右侧文件列表 + 地址栏 +
// 底部「文件夹:」输入框 + 「选择文件夹 / 取消」，还能直接新建文件夹。
//
// 坑：shell 对话框要求调用线程是 **STA**（坑 #28）。.NET 主线程默认是 COM MTA，
// MTA 下对话框会被创建却**始终不显示**（WS_VISIBLE 没置上）。所以这里统一放到自建的
// STA 线程执行，调用方线程在等待期间继续泵消息，UI 不会卡死。
//
// 坑：Marshal.GetObjectForIUnknown **不会** AddRef —— RCW 直接接管这份引用。
// 之前多写了一句 Marshal.Release，等于把对象提前 Release 掉：对话框虽然能弹出来，
// 但一关闭对象就死了，GetResult 拿不到结果（返回 null），于是又退回旧树形窗。
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
    static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>弹出文件夹选择对话框；返回选中目录的绝对路径，取消返回 null。</summary>
    public static string? Pick(IntPtr owner, string title)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return PickCore(owner, title);

        string? result = null;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { result = PickCore(owner, title); }
            catch (Exception ex) { err = ex; }
        })
        {
            IsBackground = true,
            Name = "MiniLaunch.FolderPicker",
        };
        t.SetApartmentState(ApartmentState.STA);   // 必须在启动线程前设置
        t.Start();

        // 等待期间继续泵消息：对话框在另一个线程上跑模态循环，本线程不能停摆
        while (t.IsAlive)
        {
            while (Win32.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
        if (err != null) return null;
        return result;
    }

    /// <summary>在 STA 线程上执行真正的对话框调用。</summary>
    static string? PickCore(IntPtr owner, string title)
    {
        // 本线程必须先初始化成 STA，否则 shell 对话框会创建却不显示
        bool comInit = false;
        try { comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0; }
        catch { comInit = false; }
        try
        {
            // 优先用现代对话框；系统不支持（极老的 Windows）时退回旧树形窗
            if (TryCreateModernDialog(out var dlg))
                return RunModernDialog(dlg, owner);
            return PickLegacy(owner, title);
        }
        finally
        {
            if (comInit) { try { Win32.CoUninitialize(); } catch { } }
        }
    }

    /// <summary>创建现代文件夹选择对话框对象（CLSID_FileOpenDialog）。</summary>
    static bool TryCreateModernDialog(out IFileOpenDialog dialog)
    {
        dialog = null!;
        try
        {
            Guid clsid = ClsidFileOpenDialog;
            Guid iid = IidFileOpenDialog;
            int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero,
                Win32.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);
            if (hr < 0 || ppv == IntPtr.Zero) return false;

            // 注意：Marshal.GetObjectForIUnknown 不会 AddRef，RCW 接管这份引用，
            // 千万别再 Marshal.Release —— 提前释放会让对象在对话框关闭后失效。
            var raw = Marshal.GetObjectForIUnknown(ppv);
            dialog = (IFileOpenDialog)raw;

            // 设为「只选文件夹」模式
            dialog.GetOptions(out uint opts);
            opts |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_NOCHANGEDIR;
            dialog.SetOptions(opts);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>显示现代对话框并取回选中的目录。返回 null = 用户取消。</summary>
    static string? RunModernDialog(IFileOpenDialog dlg, IntPtr owner)
    {
        try
        {
            int hrShow = dlg.Show(owner);
            if (hrShow < 0) return null;   // 用户取消（0x800704C7）

            int hrGet = dlg.GetResult(out IntPtr pItem);
            if (hrGet < 0 || pItem == IntPtr.Zero) return null;
            try
            {
                // ComImport 接口里用 `out IShellItem` 参数会被 CLR 封送成 null，
                // 所以这里取裸指针后自己 QI。
                Guid iidItem = IidShellItem;
                int hrQi = Marshal.QueryInterface(pItem, ref iidItem, out IntPtr pShell);
                if (hrQi < 0 || pShell == IntPtr.Zero) return null;
                // 同理：GetObjectForIUnknown 接管引用，不要再 Release
                var item = (IShellItem)Marshal.GetObjectForIUnknown(pShell);
                int hrName = item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pName);
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
                // pItem 是 GetResult 交给我们的引用，必须由我们释放
                try { Marshal.Release(pItem); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>旧版树形对话框（SHBrowseForFolder）——仅作为现代对话框不可用时的兜底。</summary>
    static string? PickLegacy(IntPtr owner, string title)
    {
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

    /// <summary>自检用：确认系统能创建现代文件夹选择对话框（IFileDialog 可用）。</summary>
    public static bool CanCreateModernDialog()
    {
        bool ok = false;
        LastCreateHr = unchecked((int)0x80004005);
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
                        var dlg = (IFileOpenDialog)Marshal.GetObjectForIUnknown(ppv);
                        dlg.GetOptions(out uint opts);
                        opts |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_NOCHANGEDIR;
                        int hrOpt = dlg.SetOptions(opts);
                        LastSetOptionsHr = hrOpt;
                        if (hrOpt < 0) ok = false;
                    }
                }
                finally { if (comInit) { try { Win32.CoUninitialize(); } catch { } } }
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
        var t = new Thread(() =>
        {
            try
            {
                bool comInit = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED) >= 0;
                try
                {
                    Guid clsid = ClsidFileOpenDialog;
                    Guid iid = IidFileOpenDialog;
                    int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero,
                        Win32.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);
                    if (hr < 0 || ppv == IntPtr.Zero) return;
                    var dlg = (IFileOpenDialog)Marshal.GetObjectForIUnknown(ppv);
                    int hrGet = dlg.GetResult(out IntPtr pItem);
                    LastResultSlotHr = hrGet;
                    ok = hrGet == unchecked((int)0x8000FFFF);   // E_UNEXPECTED
                }
                finally { if (comInit) { try { Win32.CoUninitialize(); } catch { } } }
            }
            catch { }
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

    // ===== COM 接口（手工声明，不引任何 NuGet / 反射）=====

    /// <summary>IShellItem：取文件系统路径。</summary>
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, uint riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }

    /// <summary>
    /// IFileOpenDialog：方法顺序必须与 vtable 完全一致（少一个都会调到错的方法）。
    ///
    /// 注意：微软文档里的方法列表是**按字母排的**，不是 vtable 顺序！实际顺序是
    /// （本机 Win11 逐槽实测确认，槽号 = IUnknown 占 3 槽后的偏移）：
    ///   3 Show / 4 SetFileTypes / 5 SetFileTypeIndex / 6 GetFileTypeIndex /
    ///   7 Advise / 8 Unadvise / 9 SetOptions / 10 GetOptions /
    ///   11 SetFolder / 12 SetDefaultFolder / 13 GetFolder / 14 GetCurrentSelection /
    ///   15 SetFileName / 16 GetFileName / 17 SetFileNameLabel /
    ///   18 SetDefaultExtension / 19 Close / 20 GetResult
    /// 按文档顺序写会把 GetResult 写到槽 19（Close）上 → 取不到结果。
    /// </summary>
    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetFolder(IntPtr psi);
        [PreserveSig] int SetDefaultFolder(IntPtr psi);
        [PreserveSig] int GetFolder(out IntPtr ppsi);
        [PreserveSig] int GetCurrentSelection(out IntPtr ppsi);
        [PreserveSig] int SetFileName(IntPtr pszName);
        [PreserveSig] int GetFileName(out IntPtr pszName);
        [PreserveSig] int SetFileNameLabel(IntPtr pszLabel);
        [PreserveSig] int SetDefaultExtension(IntPtr pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int GetResult(out IntPtr ppsi);
    }
}
