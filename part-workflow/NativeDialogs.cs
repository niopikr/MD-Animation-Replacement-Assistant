using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PartWorkflow;

public sealed record FolderPickerRequest(string? InitialPath);

public static class NativeDialogs
{
    private const int AfterWindowMessage = 12; // WH_CALLWNDPROCRET
    private const uint ShowWindowMessage = 0x18; // WM_SHOWWINDOW
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint FolderOptions = 0x20 | 0x40 | 0x800 | 0x8; // PickFolders, ForceFileSystem, PathMustExist, NoChangeDir
    private static readonly Guid FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    public static Task<string?> PickFolder(string title, string? initialPath)
    {
        var foreground = GetForegroundWindow();
        var owner = BrowserOwner(foreground);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(ShowFolderPicker(title, initialPath, owner, foreground)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static nint BrowserOwner(nint foreground)
    {
        if (foreground == 0) return 0;
        var name = new StringBuilder(64);
        GetClassNameW(foreground, name, name.Capacity);
        if (name.ToString() is not ("Chrome_WidgetWin_1" or "MozillaWindowClass")) return 0;
        var caption = new StringBuilder(256);
        GetWindowTextW(foreground, caption, caption.Capacity);
        return caption.ToString().Contains("MD动画替换助手", StringComparison.Ordinal) ? foreground : 0;
    }

    private static string? ShowFolderPicker(string title, string? initialPath, nint owner, nint foreground)
    {
        var previousDpi = SetThreadDpiAwarenessContext(-4); // Per-monitor V2.
        nint fallbackOwner = 0;
        IFileDialog? picker = null;
        try
        {
            if (owner == 0 || !IsWindow(owner))
            {
                // An owned dialog does not need a separate taskbar button.
                fallbackOwner = CreateWindowExW(0x80, "STATIC", "", 0x80000000, 0, 0, 0, 0, 0, 0, 0, 0);
                if (fallbackOwner == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建文件夹对话框的父窗口。");
                owner = fallbackOwner;
            }
            picker = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOpenDialog, throwOnError: true)!)!;
            picker.SetTitle(title);
            picker.GetOptions(out var options);
            picker.SetOptions(options | FolderOptions);
            var path = initialPath?.Trim().Trim('"');
            if (!Directory.Exists(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(path))
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(path!, 0, in iid, out var initial);
                try { picker.SetFolder(initial); }
                finally { Marshal.ReleaseComObject(initial); }
            }

            var placed = false;
            HookProc promote = (code, sentByThread, messagePointer) =>
            {
                if (code >= 0 && !placed)
                {
                    var message = Marshal.PtrToStructure<WindowMessage>(messagePointer);
                    if (message.Message == ShowWindowMessage && message.WParam != 0 &&
                        GetWindow(message.Window, 4) == owner) // GW_OWNER
                    {
                        var windowClass = new StringBuilder(32);
                        GetClassNameW(message.Window, windowClass, windowClass.Capacity);
                        if (windowClass.ToString() == "#32770")
                        {
                            placed = true;
                            PlaceFolderWindow(message.Window, foreground, owner);
                        }
                    }
                }
                return CallNextHookEx(0, code, sentByThread, messagePointer);
            };
            var hook = SetWindowsHookExW(AfterWindowMessage, promote, 0, GetCurrentThreadId());
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将文件夹对话框置于前台。");
            int result;
            try { result = picker.Show(owner); }
            finally
            {
                UnhookWindowsHookEx(hook);
                GC.KeepAlive(promote);
            }
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            picker.GetResult(out var folder);
            try
            {
                folder.GetDisplayName(0x80058000, out var display); // SIGDN_FILESYSPATH
                try { return Marshal.PtrToStringUni(display); }
                finally { Marshal.FreeCoTaskMem(display); }
            }
            finally { Marshal.ReleaseComObject(folder); }
        }
        finally
        {
            if (picker is not null) Marshal.ReleaseComObject(picker);
            if (fallbackOwner != 0) DestroyWindow(fallbackOwner);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static void PlaceFolderWindow(nint window, nint foreground, nint owner)
    {
        var monitor = MonitorFromWindow(foreground != 0 ? foreground : window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfoW(monitor, ref info))
        {
            var work = info.Work;
            var workWidth = work.Right - work.Left;
            var workHeight = work.Bottom - work.Top;
            var width = workWidth * 90 / 100;
            var height = workHeight * 95 / 100;
            SetWindowPos(window, -1, work.Left + (workWidth - width) / 2, work.Top + (workHeight - height) / 2,
                width, height, 0x200); // SWP_NOOWNERZORDER
        }
        else
            SetWindowPos(window, -1, 0, 0, 0, 0, 0x200 | 0x1 | 0x2);
        if (SetForegroundWindow(window) || GetForegroundWindow() != owner) return;

        var dialogThread = GetCurrentThreadId();
        var ownerThread = GetWindowThreadProcessId(owner, 0);
        if (ownerThread != 0 && ownerThread != dialogThread && AttachThreadInput(dialogThread, ownerThread, true))
        {
            try { SetForegroundWindow(window); }
            finally { AttachThreadInput(dialogThread, ownerThread, false); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public nint Result, LParam, WParam;
        public uint Message;
        public nint Window;
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, nint specifications);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem result);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out nint name);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out IShellItem item);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, nint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint target, bool enabled);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    public static void Error(string text) => MessageBoxW(0, text, "MD动画替换助手", 0x10);
}
