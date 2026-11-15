namespace ProcPilot.Services;

/// <summary>
/// 轮询兜底实现：当 ETW 会话无法建立（提权不足 / 会话被占用 / 系统限制）时使用。
///
/// 只做"名字集合 diff"，每轮仅一次 NtQuerySystemInformation 系统调用（约 0.1~0.3ms），
/// 不打开任何进程句柄，因此即使 2s 一轮，CPU 占用也可忽略（约 0.01%）。
/// </summary>
public sealed class PollingProcessWatcher : IProcessWatcher
{
    private readonly SystemProcessSnapshotSource _source = new();
    private readonly int _intervalMs;
    private readonly object _lock = new();
    private Timer? _timer;
    private HashSet<int> _known = new();
    private bool _primed;
    private volatile bool _running;

    public string Mode => $"轮询兜底（{_intervalMs / 1000.0:0.#}s/次，仅读名字）";
    public bool IsPolling => true;
    public string LastError { get; private set; } = "";

    public event Action<ProcessStartedEvent>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    public PollingProcessWatcher(int intervalMs = 2000)
    {
        _intervalMs = Math.Max(500, intervalMs);
    }

    public bool Start()
    {
        if (_running) return true;
        try
        {
            _known = new HashSet<int>(_source.Collect().Select(e => e.Pid));
            _primed = true;
            _running = true;
            _timer = new Timer(_ => Poll(), null, _intervalMs, _intervalMs);
            DiagLog.Write($"PollingProcessWatcher: 启动，间隔 {_intervalMs}ms，初始 {_known.Count} 个进程");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagLog.WriteException("PollingProcessWatcher.Start", ex);
            return false;
        }
    }

    private void Poll()
    {
        if (!_running) return;
        try
        {
            List<ProcEntry> entries;
            lock (_lock) entries = _source.Collect();
            var current = new HashSet<int>(entries.Count);
            var names = new Dictionary<int, string>(entries.Count);
            foreach (var e in entries)
            {
                current.Add(e.Pid);
                names[e.Pid] = e.Name;
            }

            List<int> started = new();
            List<int> stopped = new();
            lock (_lock)
            {
                if (!_primed)
                {
                    _known = current;
                    _primed = true;
                    return;
                }
                foreach (var pid in current)
                    if (!_known.Contains(pid)) started.Add(pid);
                foreach (var pid in _known)
                    if (!current.Contains(pid)) stopped.Add(pid);
                _known = current;
            }

            foreach (var pid in started)
                ProcessStarted?.Invoke(new ProcessStartedEvent(pid, null, names.TryGetValue(pid, out var n) ? n : null));
            foreach (var pid in stopped) ProcessStopped?.Invoke(pid);
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("PollingProcessWatcher.Poll", ex);
        }
    }

    public void Dispose()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
        lock (_lock) _source.Dispose();
    }
}
