using ProcPilot.Models;

namespace ProcPilot.Services;

/// <summary>
/// 自动规则引擎（需求1的核心）：
/// 进程启动 → 命中"记忆规则" → 自动应用记住的优先级/冻结状态。
///
/// 事件源优先 ETW（系统推送），失败自动降级为轻量轮询。
/// 已知系统实现细节：部分程序在自己的初始化阶段会把优先级改回来（典型如启动器拉起的游戏、
/// 自带服务的客户端），所以除"启动即应用"外，还会在 2.5s 后复核一次优先级，被改回则再应用一次。
/// </summary>
public sealed class RuleEngine : IDisposable
{
    private readonly ProcessController _controller;
    private readonly RuleStore _rules;
    private readonly AppSettings _settings;
    private readonly SystemProcessSnapshotSource _source = new();
    private IProcessWatcher? _watcher;
    private readonly HashSet<int> _pending = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>规则被自动应用后触发（参数：pid、状态、进程名）。可能在任意线程。</summary>
    public event Action<int, ProcessStateKind, string>? AutoApplied;

    /// <summary>引擎状态说明（界面展示）。</summary>
    public string Status { get; private set; } = "未启动";

    public bool IsPolling => _watcher?.IsPolling ?? true;

    public RuleEngine(ProcessController controller, RuleStore rules, AppSettings settings)
    {
        _controller = controller;
        _rules = rules;
        _settings = settings;
    }

    public void Start()
    {
        if (_disposed) return;
        try
        {
            var etw = new EtwProcessWatcher();
            if (etw.Start())
            {
                _watcher = etw;
            }
            else
            {
                var err = etw.LastError;
                etw.Dispose();
                DiagLog.Write($"ETW 启动失败（{err}），降级为轮询");
                var polling = new PollingProcessWatcher(2000);
                _watcher = polling.Start() ? polling : null;
                if (_watcher == null) Status = "事件源不可用";
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("RuleEngine.Start", ex);
            _watcher = null;
        }

        if (_watcher != null)
        {
            _watcher.ProcessStarted += OnProcessStarted;
            _watcher.ProcessStopped += OnProcessStopped;
            Status = _watcher.Mode;
        }
        else
        {
            Status = "事件源不可用（规则不会自动应用）";
        }

        DiagLog.Write($"RuleEngine: 事件源 = {Status}，规则 {_rules.Count} 条");

        if (_settings.ApplyRulesToRunningAtStartup)
            _ = Task.Run(ApplyToRunning);
    }

    /// <summary>对已经在运行且命中规则的进程应用一次（ProcPilot 自身启动时）。</summary>
    public void ApplyToRunning()
    {
        int applied = 0;
        try
        {
            var entries = _source.Collect();
            foreach (var e in entries)
            {
                if (e.Pid == Environment.ProcessId) continue;
                if (!_rules.TryGet(e.Name, out var rule) || !rule.Enabled) continue;
                if (rule.State == ProcessStateKind.Normal) continue;

                var (ok, err) = _controller.ApplyState(e.Pid, rule.State);
                if (ok)
                {
                    applied++;
                    AutoApplied?.Invoke(e.Pid, rule.State, e.Name);
                }
                else
                {
                    DiagLog.Write($"ApplyToRunning 失败 {e.Name}({e.Pid}): {err}");
                }
                Thread.Sleep(15); // 错峰，避免瞬间对几十个进程同时操作
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("RuleEngine.ApplyToRunning", ex);
        }
        DiagLog.Write($"RuleEngine.ApplyToRunning: 已对 {applied} 个在运行的进程应用规则");
    }

    private void OnProcessStarted(ProcessStartedEvent e)
    {
        if (_disposed || e.Pid <= 0) return;
        Task.Run(() => HandleStartAsync(e));
    }

    private void OnProcessStopped(int pid)
    {
        lock (_lock) _pending.Remove(pid);
    }

    private void HandleStartAsync(ProcessStartedEvent e)
    {
        int pid = e.Pid;
        try
        {
            // ETW 事件里通常直接带着映像名；轮询兜底也有名字；都没有时再退化为打开句柄查询
            string name = e.Name ?? "";
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(e.ImagePath))
                name = System.IO.Path.GetFileNameWithoutExtension(e.ImagePath);
            if (string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < 8; i++)
                {
                    var path = NativeMethods.TryGetImagePath(pid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        name = System.IO.Path.GetFileNameWithoutExtension(path);
                        break;
                    }
                    Thread.Sleep(150);
                }
            }
            if (string.IsNullOrEmpty(name)) return;

            if (!_rules.TryGet(name, out var rule) || !rule.Enabled || rule.State == ProcessStateKind.Normal)
                return;

            lock (_lock)
            {
                if (_pending.Contains(pid)) return; // 同一次启动不重复处理
                _pending.Add(pid);
            }

            if (rule.DelayMs > 0) Thread.Sleep(Math.Min(rule.DelayMs, 10000));

            var (ok, err) = _controller.ApplyState(pid, rule.State);
            if (!ok)
            {
                DiagLog.Write($"规则应用失败 {name}({pid}) -> {rule.State}: {err}");
                return;
            }
            DiagLog.Write($"规则应用成功 {name}({pid}) -> {rule.State}");
            AutoApplied?.Invoke(pid, rule.State, name);

            // 冻结不需要复核（重复挂起会累加挂起计数）；优先级类在 2.5s 后复核一次
            if (rule.State is ProcessStateKind.BelowNormal or ProcessStateKind.BelowNormalTrimmed)
                _ = Task.Run(() => VerifyAfterDelay(pid, name, rule.State, 2500));
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("RuleEngine.HandleStartAsync", ex);
        }
    }

    private void VerifyAfterDelay(int pid, string name, ProcessStateKind target, int delayMs)
    {
        try
        {
            Thread.Sleep(delayMs);
            if (_disposed) return;
            if (_controller.GetState(pid) != target) return; // 用户手动改过，尊重用户

            uint cls = NativeMethods.TryGetPriorityClass(pid);
            if (cls == 0) return; // 进程已退出

            uint expected = target == ProcessStateKind.Frozen
                ? NativeMethods.NORMAL_PRIORITY_CLASS
                : NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
            if (cls == expected) return;

            var (ok, _) = _controller.ApplyState(pid, target);
            if (ok)
            {
                DiagLog.Write($"规则复核：{name}({pid}) 的优先级被程序改回，已重新应用 {target}");
                AutoApplied?.Invoke(pid, target, name);
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("RuleEngine.VerifyAfterDelay", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        _source.Dispose();
    }
}
