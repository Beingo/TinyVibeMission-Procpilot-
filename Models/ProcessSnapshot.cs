namespace ProcPilot.Models;

/// <summary>
/// 一次采样得到的进程快照（只读值类型：每秒几百条也不产生 GC 压力）。
/// </summary>
public readonly struct ProcessSnapshot
{
    public ProcessSnapshot(int pid, string name, double cpuPercent, long workingSet64,
        bool isEligible, int threadCount, ProcessStateKind state)
    {
        Pid = pid;
        Name = name;
        CpuPercent = cpuPercent;
        WorkingSet64 = workingSet64;
        IsEligible = isEligible;
        ThreadCount = threadCount;
        State = state;
    }

    public int Pid { get; }
    public string Name { get; }
    public double CpuPercent { get; }
    public long WorkingSet64 { get; }

    /// <summary>
    /// 是否满足操作资格（可操作/亮显）：预热期内全部为 true；
    /// 过滤期内为"最近 5s 内 CPU 持续 &gt;1%" 或"内存 &gt;50MB"或用户已设状态的进程。
    /// </summary>
    public bool IsEligible { get; }

    public int ThreadCount { get; }

    /// <summary>当前已应用的状态（来自 ProcessController 的会话内状态）。</summary>
    public ProcessStateKind State { get; }
}
