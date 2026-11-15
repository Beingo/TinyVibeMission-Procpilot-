namespace ProcPilot.Services;

/// <summary>进程启动事件：至少给出 PID，通常还能直接带上映像名（ETW payload 里有）。</summary>
public readonly struct ProcessStartedEvent
{
    public ProcessStartedEvent(int pid, string? imagePath, string? name)
    {
        Pid = pid;
        ImagePath = imagePath;
        Name = name;
    }

    public int Pid { get; }

    /// <summary>完整映像路径（ETW 事件里带；轮询兜底时为 null）。</summary>
    public string? ImagePath { get; }

    /// <summary>进程名（不含 .exe，尽量给出）。</summary>
    public string? Name { get; }
}

/// <summary>进程启动事件源（ETW 事件驱动 / 轮询兜底）。</summary>
public interface IProcessWatcher : IDisposable
{
    /// <summary>事件源类型（用于界面展示）。</summary>
    string Mode { get; }

    /// <summary>是否真正依赖轮询（false = 系统推送）。</summary>
    bool IsPolling { get; }

    string LastError { get; }

    event Action<ProcessStartedEvent>? ProcessStarted;

    event Action<int>? ProcessStopped;

    bool Start();
}
