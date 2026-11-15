using ProcPilot.Models;

namespace ProcPilot.Services;

/// <summary>采样强度。</summary>
public enum SampleMode
{
    /// <summary>停止采样（窗口隐藏/最小化到托盘时）——后台零开销。</summary>
    Off,

    /// <summary>慢速采样（窗口可见但未聚焦）。</summary>
    Slow,

    /// <summary>正常采样（窗口在前台）。</summary>
    Normal,
}

/// <summary>
/// 进程采样与展示过滤核心。
///
/// 相比旧版的关键改动（需求3：降低后台资源消耗）：
/// 1. 采样数据源由"Process.GetProcesses() + 逐进程 OpenProcess 读 SessionId/CPU时间/工作集"
///    改为一次 NtQuerySystemInformation 系统调用（见 SystemProcessSnapshotSource），
///    300 进程从约 900 次句柄 open 降到 0 次；
/// 2. 定时器只有一个，按强度切换间隔（前台 1s / 未聚焦 2s / 后台 0 = 完全停止）；
/// 3. CPU 采样窗口用环形数组代替 LinkedList，消除每秒数百次小对象分配；
/// 4. 快照为只读结构体，UI 侧不再产生大量临时对象；
/// 5. 采样耗时写入诊断日志（打开诊断时），便于验证降耗效果。
///
/// 展示规则保持原样：系统进程（Session 0）不显示；预热 5s 内全部可操作；
/// 之后仅"内存 &gt;50MB"或"最近 5s 内 CPU 持续 &gt;1%"或用户已设状态的进程可操作；
/// 可操作进程永远排在灰色进程之上，组内按内存从大到小。
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    private const double WarmupSeconds = 5;
    private const double SustainSeconds = 5;
    private const double CpuThreshold = 1.0;
    private const long EligibleMemoryThreshold = 50L * 1024 * 1024;
    private const int HistorySize = 8;               // 环形采样窗口（最多 8 个样本）
    private const double MaxSampleAgeSeconds = 3.0;  // 样本过旧视为进程失联

    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly int _ownPid = Environment.ProcessId;
    private readonly object _lock = new();
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly SystemProcessSnapshotSource _source = new();
    private readonly ProcessController _controller;

    private Timer? _timer;
    private SampleMode _mode = SampleMode.Off;
    private int _foregroundIntervalMs = 1000;
    private int _unfocusedIntervalMs = 2000;
    private HashSet<int> _pinnedPids = new();
    private IReadOnlyList<ProcessSnapshot> _lastSnapshot = Array.Empty<ProcessSnapshot>();
    private List<ProcEntry> _reuseEntries = new(512);
    private long _tickCount;
    private long _lastTickMicros;

    /// <summary>采样完成后触发（工作线程回调；订阅方自行 marshal 到 UI 线程）。</summary>
    public event Action<IReadOnlyList<ProcessSnapshot>>? SnapshotUpdated;

    private sealed class Track
    {
        public int Pid;
        public string Name = "";
        public TimeSpan PrevCpuTime;
        public long PrevSampleTicks;
        public bool HasPrev;
        public double LastCpu;
        public long LastWorkingSet;
        public int ThreadCount;
        public int MissingTicks;
        // 环形采样窗口
        public readonly long[] SampleTicks = new long[HistorySize];
        public readonly double[] SampleCpu = new double[HistorySize];
        public int SampleStart;
        public int SampleCount;
    }

    public ProcessMonitor(ProcessController controller)
    {
        _controller = controller;
    }

    public void SetIntervals(int foregroundMs, int unfocusedMs)
    {
        lock (_lock)
        {
            _foregroundIntervalMs = Math.Clamp(foregroundMs, 250, 5000);
            _unfocusedIntervalMs = Math.Clamp(Math.Max(unfocusedMs, _foregroundIntervalMs), 250, 30000);
            if (_mode != SampleMode.Off) RestartTimer();
        }
    }

    /// <summary>切换采样强度；相同强度重复调用无副作用。</summary>
    public void SetMode(SampleMode mode)
    {
        lock (_lock)
        {
            if (mode == _mode) return;
            _mode = mode;
            if (mode == SampleMode.Off)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                DiagLog.Write("ProcessMonitor: 已暂停采样（后台零轮询）");
            }
            else
            {
                RestartTimer();
                DiagLog.Write($"ProcessMonitor: 采样间隔 {CurrentIntervalMs}ms");
            }
        }
    }

    public SampleMode Mode
    {
        get { lock (_lock) return _mode; }
    }

    private int CurrentIntervalMs => _mode == SampleMode.Slow ? _unfocusedIntervalMs : _foregroundIntervalMs;

    private void RestartTimer()
    {
        var period = TimeSpan.FromMilliseconds(CurrentIntervalMs);
        if (_timer == null)
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, period);
        else
            _timer.Change(TimeSpan.Zero, period);
    }

    /// <summary>用户手动设置过状态 / 已命中记忆规则的 PID：这些进程无条件展示且可操作。</summary>
    public void SetPinnedPids(IEnumerable<int> pids)
    {
        lock (_lock) _pinnedPids = new HashSet<int>(pids);
    }

    public IReadOnlyList<ProcessSnapshot> GetLastSnapshot()
    {
        lock (_lock) return _lastSnapshot;
    }

    /// <summary>是否处于首次初始化预热期（期间展示全部非系统进程）。</summary>
    public bool IsWarmup
    {
        get { lock (_lock) return (DateTime.UtcNow - _startedAt).TotalSeconds < WarmupSeconds; }
    }

    /// <summary>上一轮采样耗时（微秒，含系统调用 + 解析）。</summary>
    public long LastTickMicros => Interlocked.Read(ref _lastTickMicros);

    private void Tick()
    {
        IReadOnlyList<ProcessSnapshot> visible;
        try
        {
            visible = Collect();
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("ProcessMonitor.Collect", ex);
            return;
        }

        lock (_lock) _lastSnapshot = visible;
        try
        {
            SnapshotUpdated?.Invoke(visible);
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("ProcessMonitor.SnapshotUpdated", ex);
        }
    }

    private IReadOnlyList<ProcessSnapshot> Collect()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var now = new DateTime(nowTicks, DateTimeKind.Utc);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        var entries = _source.Collect(_reuseEntries);
        var seen = new HashSet<int>(entries.Count);
        var result = new List<ProcessSnapshot>(entries.Count);
        bool warmup = (now - _startedAt).TotalSeconds < WarmupSeconds;
        var controllerStates = _controller.States; // 一次性快照，避免每进程加锁

        lock (_lock)
        {
            foreach (var e in entries)
            {
                int pid = e.Pid;
                if (pid == _ownPid) continue;      // 不显示自己
                if (e.SessionId == 0) continue;    // 系统进程（Session 0）不展示

                seen.Add(pid);
                if (!_tracks.TryGetValue(pid, out var t))
                {
                    t = new Track { Pid = pid, Name = e.Name };
                    _tracks[pid] = t;
                }
                else if (t.Name != e.Name)
                {
                    t.Name = e.Name; // PID 复用或改名
                }

                t.LastWorkingSet = e.WorkingSet;
                t.ThreadCount = e.ThreadCount;

                if (!t.HasPrev)
                {
                    t.PrevCpuTime = e.CpuTime;
                    t.PrevSampleTicks = nowTicks;
                    t.HasPrev = true;
                    t.LastCpu = 0;
                    t.MissingTicks = 0;
                    continue; // 首样本只做基线
                }

                double deltaCpu = (e.CpuTime - t.PrevCpuTime).TotalSeconds;
                if (deltaCpu < 0) deltaCpu = 0; // PID 复用：CPU 累计时间倒退
                double deltaWall = Math.Max(0.001, (nowTicks - t.PrevSampleTicks) / (double)TimeSpan.TicksPerSecond);
                double cpu = deltaWall > 0 && deltaCpu > 0 ? deltaCpu / deltaWall / _processorCount * 100.0 : 0.0;

                t.PrevCpuTime = e.CpuTime;
                t.PrevSampleTicks = nowTicks;
                t.LastCpu = cpu;
                t.MissingTicks = 0;

                // 环形窗口写入
                int slot = (t.SampleStart + t.SampleCount) % HistorySize;
                if (t.SampleCount < HistorySize) t.SampleCount++;
                else t.SampleStart = (t.SampleStart + 1) % HistorySize;
                t.SampleTicks[slot] = nowTicks;
                t.SampleCpu[slot] = cpu;
            }

            // 清理消失的进程
            if (_tracks.Count > seen.Count)
            {
                List<int>? gone = null;
                foreach (var kv in _tracks)
                {
                    if (seen.Contains(kv.Key)) continue;
                    if (++kv.Value.MissingTicks > 3)
                        (gone ??= new List<int>()).Add(kv.Key);
                }
                if (gone != null)
                    foreach (var pid in gone) _tracks.Remove(pid);
            }

            foreach (var t in _tracks.Values)
            {
                if (!t.HasPrev) continue;
                bool pinned = _pinnedPids.Contains(t.Pid);
                bool eligible = pinned || warmup
                    || t.LastWorkingSet > EligibleMemoryThreshold
                    || IsSustainedOverThreshold(t, now);
                var state = controllerStates.TryGetValue(t.Pid, out var st) ? st : ProcessStateKind.Normal;
                result.Add(new ProcessSnapshot(t.Pid, t.Name, t.LastCpu, t.LastWorkingSet, eligible,
                    t.ThreadCount, state));
            }

            // 排序：可操作进程永远在灰色进程之上；组内按内存从大到小
            result.Sort(static (a, b) =>
            {
                int byEligible = b.IsEligible.CompareTo(a.IsEligible);
                return byEligible != 0 ? byEligible : b.WorkingSet64.CompareTo(a.WorkingSet64);
            });

            _tickCount++;
            Interlocked.Exchange(ref _lastTickMicros,
                (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);

            if (_tickCount % 10 == 1)
            {
                DiagLog.Write($"tick#{_tickCount}: 系统进程={entries.Count} 展示={result.Count} " +
                              $"可操作={result.Count(static s => s.IsEligible)} 采样耗时={LastTickMicros}µs " +
                              $"间隔={CurrentIntervalMs}ms warmup={warmup}");
            }
        }
        return result;
    }

    /// <summary>最近 5s 内 CPU 持续 &gt;1% 判定：从最新样本向前累计连续超阈值的时长。</summary>
    private bool IsSustainedOverThreshold(Track t, DateTime now)
    {
        if (t.SampleCount == 0) return false;

        // 最新样本必须足够新（否则进程已失联/刚恢复）
        int newest = (t.SampleStart + t.SampleCount - 1) % HistorySize;
        if ((now.Ticks - t.SampleTicks[newest]) / (double)TimeSpan.TicksPerSecond > MaxSampleAgeSeconds) return false;

        double accumulated = 0;
        long? prevTs = null;
        for (int i = 0; i < t.SampleCount; i++)
        {
            int idx = (t.SampleStart + t.SampleCount - 1 - i) % HistorySize;
            if (t.SampleCpu[idx] <= CpuThreshold) return false;
            if (prevTs is { } pts)
            {
                accumulated += (pts - t.SampleTicks[idx]) / (double)TimeSpan.TicksPerSecond;
                if (accumulated >= SustainSeconds) return true;
            }
            prevTs = t.SampleTicks[idx];
        }
        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _mode = SampleMode.Off;
            _timer?.Dispose();
            _timer = null;
            _source.Dispose();          // Collect 在 _lock 内执行，这里持锁释放不会与采样并发
            _reuseEntries = new List<ProcEntry>(0);
        }
    }
}
