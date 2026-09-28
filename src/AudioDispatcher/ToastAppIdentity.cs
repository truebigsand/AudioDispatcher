using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AudioDispatcher.Logging;

namespace AudioDispatcher;

/// <summary>
/// 注册应用的通知身份(稳定 AUMID),须在创建托盘/窗口前调用:
/// 1) 进程级 AUMID:托盘气泡转系统 Toast 时按此归档,不依赖系统生成的哈希身份;
/// 2) 开始菜单快捷方式携带 System.AppUserModel.ID:Win11 显示 Toast 前按 AUMID
///    解析应用信息,解析不到(无快捷方式)时通知会被记录但横幅不显示。
/// </summary>
internal static class ToastAppIdentity
{
    public const string AumId = "AudioDispatcher";

    public static void Register()
    {
        var hr = SetCurrentProcessExplicitAppUserModelID(AumId);
        if (hr != 0)
        {
            AppLog.Warn($"AUMID 注册失败: 0x{hr:X8},托盘气泡将沿用系统生成的身份");
            return;
        }
        try
        {
            EnsureStartMenuShortcut();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"开始菜单快捷方式创建失败,Toast 可能不显示: {ex.Message}");
        }
    }

    private static void EnsureStartMenuShortcut()
    {
        var lnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "AudioDispatcher.lnk");
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        // 每次启动重写以自愈:exe 挪位置后旧快捷方式目标会失效,导致开始菜单
        // 启动失败且 Toast 身份解析指向旧路径。写入量极小,不值得做差异检测。
        var shellLink = (IShellLinkW)new ShellLinkRCW();
        shellLink.SetPath(exe);
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
        shellLink.SetIconLocation(exe, 0);

        // System.AppUserModel.ID = AumId
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        var store = (IPropertyStore)shellLink;
        // SetValue 按约定复制值,PROPVARIANT 内的堆内存由调用方释放
        var pv = PropVariant.FromString(AumId);
        try
        {
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally
        {
            pv.Free();
        }

        ((IPersistFile)shellLink).Save(lnk, fRemember: true);
        AppLog.Info($"通知身份快捷方式已就绪: {lnk}");
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkRCW
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        private const ushort VtLpwstr = 31;

        [FieldOffset(0)] private ushort _vt;
        [FieldOffset(8)] private IntPtr _pointer;

        public static PropVariant FromString(string value)
        {
            return new PropVariant
            {
                _vt = VtLpwstr,
                _pointer = Marshal.StringToCoTaskMemUni(value),
            };
        }

        /// <summary>释放 FromString 分配的非托管字符串内存。</summary>
        public void Free()
        {
            if (_pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_pointer);
                _pointer = IntPtr.Zero;
            }
        }
    }
}
