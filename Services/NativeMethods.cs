using System.Runtime.InteropServices;
using System.Text;

namespace ProcPilot.Services;

/// <summary>
/// Win32 / ntdll P/Invoke 封装：进程句柄、优先级、工作集、挂起/恢复，
/// 以及"一次系统调用拿到全部进程"所需的 NtQuerySystemInformation。
/// </summary>
internal static class NativeMethods
{
    // ---- 进程访问权限位 ----
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_SET_QUOTAS = 0x0100;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_VM_READ = 0x0010;

    /// <summary>Vista+ 低权限查询（路径/优先级查询用，系统/受保护进程也能开）。</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>操作完整能力集合（供状态控制使用）。</summary>
    public const uint PROCESS_ALL_ACCESS = PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION
        | PROCESS_SET_QUOTAS | PROCESS_SUSPEND_RESUME | PROCESS_TERMINATE | PROCESS_VM_READ;

    /// <summary>仅查询（供采样使用，权限要求低、成功率高）。</summary>
    public const uint PROCESS_QUERY_ACCESS = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    public static extern uint NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern uint NtResumeProcess(IntPtr hProcess);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>将工作集上限设为 -1：立即将进程页面修剪换出至虚拟内存（页面文件）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    // ---- 优先级类 ----
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    public static extern bool IsWow64Process(IntPtr hProcess, out bool Wow64Process);

    // ---- 进程映像路径 ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref int lpdwSize);

    // ---- 系统进程快照：一次 NtQuerySystemInformation 拿全部进程 ----
    private const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr buffer,
        int bufferLength, out int returnLength);

    /// <summary>
    /// SYSTEM_PROCESS_INFORMATION 在 x64 下的关键字段偏移（已用 Process API 逐字段校验，见 selftest）。
    /// </summary>
    internal static class SysProcInfo
    {
        public const int NextEntryOffset = 0;
        public const int NumberOfThreads = 4;
        public const int CycleTime = 24;
        public const int CreateTime = 32;
        public const int UserTime = 40;
        public const int KernelTime = 48;
        public const int ImageNameLength = 56;   // UNICODE_STRING.Length
        public const int ImageNameBuffer = 64;   // UNICODE_STRING.Buffer（指针）
        public const int BasePriority = 72;
        public const int UniqueProcessId = 80;   // HANDLE 宽度
        public const int HandleCount = 96;
        public const int SessionId = 100;
        public const int WorkingSetSize = 144;
    }

    /// <summary>打开进程并取完整映像路径（失败返回空串）。</summary>
    public static string TryGetImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>取进程当前优先级类（失败返回 0）。</summary>
    public static uint TryGetPriorityClass(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetPriorityClass(h);
        }
        catch
        {
            return 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
