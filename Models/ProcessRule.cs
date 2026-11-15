using ProcPilot.Models;

namespace ProcPilot.Models;

/// <summary>
/// 一条"进程记忆"规则：某个进程（按进程名，不含 .exe，忽略大小写）每次启动时
/// 自动应用到记住的状态。
/// </summary>
public sealed class ProcessRule
{
    /// <summary>进程名（不含 .exe），比较时忽略大小写。</summary>
    public string Name { get; set; } = "";

    /// <summary>记住的状态；Normal 不具备记忆意义（保存时会被删除）。</summary>
    public ProcessStateKind State { get; set; } = ProcessStateKind.Normal;

    /// <summary>是否启用（关掉后保留规则但不自动应用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>最近一次看到的完整映像路径（仅用于展示/排查，匹配仍按进程名）。</summary>
    public string? ImagePath { get; set; }

    /// <summary>最近一次更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>规则生效时，进程启动后要等多久再应用（毫秒）。部分程序在初始化阶段会自行改回优先级。</summary>
    public int DelayMs { get; set; }

    public string StateText => State switch
    {
        ProcessStateKind.BelowNormal => "低于正常",
        ProcessStateKind.BelowNormalTrimmed => "低于正常·虚拟内存",
        ProcessStateKind.Frozen => "冻结",
        _ => "正常",
    };

    public string EnabledText => Enabled ? "是" : "否";

    public ProcessRule Clone() => new()
    {
        Name = Name,
        State = State,
        Enabled = Enabled,
        ImagePath = ImagePath,
        UpdatedAt = UpdatedAt,
        DelayMs = DelayMs,
    };
}
