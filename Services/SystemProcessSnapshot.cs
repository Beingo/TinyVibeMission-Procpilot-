using System.Runtime.InteropServices;

namespace ProcPilot.Services;

/// <summary>一条进程条目（值类型，避免每次采样产生大量小对象）。</summary>
public readonly struct ProcEntry
{
    public readonly int Pid;
    public readonly string Name;
    public readonly int SessionId;
    public readonly long WorkingSet;
    public readonly TimeSpan CpuTime;
    public readonly int ThreadCount;

    public ProcEntry(int pid, string name, int sessionId, long workingSet, TimeSpan cpuTime, int threadCount)
    {
        Pid = pid;
        Name = name;
        SessionId = sessionId;
        WorkingSet = workingSet;
        CpuTime = cpuTime;
        ThreadCount = threadCount;
    }
}

/// <summary>
/// 进程快照采集器（需求3的核心改造）。
///
/// 旧实现：Process.GetProcesses()（每次 new 几百个 Process 对象 + Dispose）再逐进程
/// 打开句柄读 SessionId / TotalProcessorTime / WorkingSet64 —— 300 个进程约 900 次句柄 open。
///
/// 新实现：一次 NtQuerySystemInformation(SystemProcessInformation) 把全部进程的
/// PID / 映像名 / 会话 / 工作组 / CPU 时间 / 线程数一次性取回，零句柄开销。
/// 缓冲区长期复用，只在系统进程数量显著变化时才重新分配。
/// </summary>
public sealed class SystemProcessSnapshotSource : IDisposable
{
    private IntPtr _buffer;
    private int _capacity;

    public SystemProcessSnapshotSource(int initialCapacity = 256 * 1024)
    {
        _capacity = initialCapacity;
        _buffer = Marshal.AllocHGlobal(_capacity);
    }

    /// <summary>上一次采样的耗时（微秒），用于诊断"降耗"是否有效。</summary>
    public long LastCollectMicros { get; private set; }

    public List<ProcEntry> Collect(List<ProcEntry>? reuse = null)
    {
        var result = reuse ?? new List<ProcEntry>(512);
        result.Clear();

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int length = 0;
        int status;
        for (int attempt = 0; ; attempt++)
        {
            status = NativeMethods.NtQuerySystemInformation(5 /*SystemProcessInformation*/,
                _buffer, _capacity, out length);
            if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH || (status < 0 && attempt == 0))
            {
                // 缓冲区不足：按系统建议长度扩容（留 25% 余量，减少下次扩容概率）
                int need = Math.Max(length, _capacity);
                int grow = (int)(need * 1.25) + 64 * 1024;
                if (grow <= _capacity) grow = _capacity * 2;
                Resize(grow);
                if (attempt > 8) return result; // 异常情况放弃本轮
                continue;
            }
            break;
        }

        if (status < 0) return result;

        int offset = 0;
        while (true)
        {
            long entry = _buffer.ToInt64() + offset;
            int next = Marshal.ReadInt32((IntPtr)entry + NativeMethods.SysProcInfo.NextEntryOffset);
            int threads = Marshal.ReadInt32((IntPtr)entry + NativeMethods.SysProcInfo.NumberOfThreads);
            long userTime = Marshal.ReadInt64((IntPtr)entry + NativeMethods.SysProcInfo.UserTime);
            long kernelTime = Marshal.ReadInt64((IntPtr)entry + NativeMethods.SysProcInfo.KernelTime);
            int nameLenBytes = (int)(uint)Marshal.ReadInt32((IntPtr)entry + NativeMethods.SysProcInfo.ImageNameLength) & 0xFFFF;
            IntPtr namePtr = Marshal.ReadIntPtr((IntPtr)entry + NativeMethods.SysProcInfo.ImageNameBuffer);
            long pidRaw = Marshal.ReadIntPtr((IntPtr)entry + NativeMethods.SysProcInfo.UniqueProcessId).ToInt64();
            int sessionId = Marshal.ReadInt32((IntPtr)entry + NativeMethods.SysProcInfo.SessionId);
            long workingSet = Marshal.ReadIntPtr((IntPtr)entry + NativeMethods.SysProcInfo.WorkingSetSize).ToInt64();

            string name = "";
            if (namePtr != IntPtr.Zero && nameLenBytes > 0)
            {
                try { name = Marshal.PtrToStringUni(namePtr, nameLenBytes / 2) ?? ""; }
                catch { name = ""; }
                // 内核返回的是含扩展名的映像名（smss.exe），与 Process.ProcessName 对齐去掉 .exe
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            }

            int pid = unchecked((int)pidRaw);
            if (pid > 0 && name.Length > 0)
            {
                long cpuTicks = userTime + kernelTime; // 100ns
                result.Add(new ProcEntry(
                    pid,
                    name,
                    sessionId,
                    workingSet,
                    TimeSpan.FromTicks(cpuTicks),
                    threads));
            }

            if (next == 0) break;
            offset += next;
            if (offset <= 0 || offset >= _capacity) break; // 防御：损坏数据
        }

        LastCollectMicros = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000
                            / System.Diagnostics.Stopwatch.Frequency;
        return result;
    }

    private void Resize(int newCapacity)
    {
        IntPtr nb = Marshal.AllocHGlobal(newCapacity);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = nb;
        _capacity = newCapacity;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
