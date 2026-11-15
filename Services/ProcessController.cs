using ProcPilot.Models;

namespace ProcPilot.Services;

/// <summary>
/// 进程状态控制三态机：
/// - BelowNormal         ：优先级降至低于正常（浅蓝）
/// - BelowNormalTrimmed  ：低于正常 + 移入虚拟内存（清空工作集）（蓝）
/// - Frozen              ：冻结（若已在虚拟内存则仅冻结）+ 移入虚拟内存（浅绿）
/// - Normal              ：恢复正常（解冻 + Normal 优先级）
/// 所有切换均先复位到基线再应用目标，保证幂等；应用退出时 RestoreAll 统一恢复。
/// </summary>
public sealed class ProcessController
{
    private readonly Dictionary<int, ProcessStateKind> _states = new();
    private readonly object _lock = new();

    /// <summary>进程状态被成功切换后触发（UI 线程之外调用方需自行 marshal）。</summary>
    public event Action<int, ProcessStateKind>? StateChanged;

    public IReadOnlyDictionary<int, ProcessStateKind> States
    {
        get { lock (_lock) return new Dictionary<int, ProcessStateKind>(_states); }
    }

    public ProcessStateKind GetState(int pid)
    {
        lock (_lock) return _states.TryGetValue(pid, out var s) ? s : ProcessStateKind.Normal;
    }

    public IEnumerable<int> GetControlledPids()
    {
        lock (_lock) return _states.Keys.ToList();
    }

    /// <summary>
    /// 将进程切换到目标状态。返回 (是否成功, 错误信息)。
    /// </summary>
    public (bool Ok, string Error) ApplyState(int pid, ProcessStateKind target)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, pid);
        if (h == IntPtr.Zero)
        {
            return (false, $"无法打开进程 PID={pid}（权限不足或进程已退出）");
        }

        try
        {
            // 复位到基线：解冻 + Normal 优先级
            NativeMethods.NtResumeProcess(h);
            NativeMethods.SetPriorityClass(h, NativeMethods.NORMAL_PRIORITY_CLASS);

            switch (target)
            {
                case ProcessStateKind.Normal:
                    break;

                case ProcessStateKind.BelowNormal:
                    if (!NativeMethods.SetPriorityClass(h, NativeMethods.BELOW_NORMAL_PRIORITY_CLASS))
                        return (false, $"设置优先级失败 PID={pid}（错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）");
                    break;

                case ProcessStateKind.BelowNormalTrimmed:
                    if (!NativeMethods.SetPriorityClass(h, NativeMethods.BELOW_NORMAL_PRIORITY_CLASS))
                        return (false, $"设置优先级失败 PID={pid}（错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）");
                    Trim(h, pid);
                    break;

                case ProcessStateKind.Frozen:
                    uint nt = NativeMethods.NtSuspendProcess(h);
                    if (nt != 0)
                        return (false, $"冻结失败 PID={pid}（NTSTATUS 0x{nt:X8}）");
                    Trim(h, pid); // 已移入虚拟内存则清空工作集为无操作，仍安全
                    break;
            }

            lock (_lock)
            {
                if (target == ProcessStateKind.Normal) _states.Remove(pid);
                else _states[pid] = target;
            }
            StateChanged?.Invoke(pid, target);
            return (true, "");
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>清空工作集：将页面移入虚拟内存（页面文件）；失败不致命，仅记录。</summary>
    private static void Trim(IntPtr h, int pid)
    {
        if (!NativeMethods.EmptyWorkingSet(h))
        {
            // 退回 SetProcessWorkingSetSize(-1,-1) 方式（等价操作）
            NativeMethods.SetProcessWorkingSetSize(h, new IntPtr(-1), new IntPtr(-1));
        }
    }

    /// <summary>应用退出前恢复所有受控进程到正常状态。</summary>
    public void RestoreAll()
    {
        List<int> pids;
        lock (_lock) pids = _states.Keys.ToList();

        foreach (var pid in pids)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                NativeMethods.NtResumeProcess(h);
                NativeMethods.SetPriorityClass(h, NativeMethods.NORMAL_PRIORITY_CLASS);
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }
        lock (_lock) _states.Clear();
    }
}
