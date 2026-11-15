namespace ProcPilot.Models;

/// <summary>应用设置（持久化到 %APPDATA%\ProcPilot\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>用户手动设置某进程状态后，自动记住，下次该进程启动时自动应用（需求1）。</summary>
    public bool AutoRemember { get; set; } = true;

    /// <summary>ProcPilot 自身启动时，对已经在运行且命中规则的进程也应用一次。</summary>
    public bool ApplyRulesToRunningAtStartup { get; set; } = true;

    /// <summary>开机自启动（写入当前用户 Run 注册表项，无需管理员）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>开机自启动时随机延迟启动（错开开机瞬间的资源竞争）。</summary>
    public bool RandomStartDelay { get; set; } = true;

    /// <summary>随机延迟的最大秒数（实际取 1..Max 之间的随机值）。</summary>
    public int RandomStartDelayMaxSec { get; set; } = 30;

    /// <summary>自启动时静默驻留托盘（不弹主窗口）。</summary>
    public bool StartHiddenOnAutoStart { get; set; } = true;

    /// <summary>窗口不在前台时暂停采样（最省资源）；关闭则降为 2s 慢采样。</summary>
    public bool PauseSamplingWhenUnfocused { get; set; }

    /// <summary>写诊断日志（默认关闭：避免频繁文件 IO；排查问题时再打开）。</summary>
    public bool Diagnostics { get; set; }

    /// <summary>前台采样间隔（毫秒）。</summary>
    public int ForegroundIntervalMs { get; set; } = 1000;

    /// <summary>窗口可见但未聚焦时的采样间隔（毫秒）。</summary>
    public int UnfocusedIntervalMs { get; set; } = 2000;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>把界面上可能越界的数值收敛到合理范围。</summary>
    public void Normalize()
    {
        RandomStartDelayMaxSec = Math.Clamp(RandomStartDelayMaxSec, 1, 300);
        ForegroundIntervalMs = Math.Clamp(ForegroundIntervalMs, 250, 5000);
        UnfocusedIntervalMs = Math.Clamp(UnfocusedIntervalMs, 500, 30000);
        if (UnfocusedIntervalMs < ForegroundIntervalMs) UnfocusedIntervalMs = ForegroundIntervalMs;
    }
}
