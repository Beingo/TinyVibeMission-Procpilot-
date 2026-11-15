namespace ProcPilot.Models;

/// <summary>
/// 用户为进程设定的状态（三态机）。
/// </summary>
public enum ProcessStateKind
{
    /// <summary>正常（未操作）</summary>
    Normal,

    /// <summary>状态2：优先级降至低于正常（BelowNormal）</summary>
    BelowNormal,

    /// <summary>状态3：低于正常 + 移入虚拟内存（清空工作集）</summary>
    BelowNormalTrimmed,

    /// <summary>状态4：冻结（已在虚拟内存则仅冻结）+ 移入虚拟内存</summary>
    Frozen,
}
