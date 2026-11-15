using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ProcPilot.Models;

/// <summary>UI 展示模型（可变，保留对象引用以维持选中态）。</summary>
public sealed class ProcessInfo : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";

    private double _cpuPercent;
    private long _memoryBytes;
    private ProcessStateKind _state;
    private bool _isEligible = true;
    private string _autoText = "";

    public double CpuPercent
    {
        get => _cpuPercent;
        set { if (Math.Abs(_cpuPercent - value) > 0.01) { _cpuPercent = value; OnPropertyChanged(nameof(CpuText)); } }
    }

    public long MemoryBytes
    {
        get => _memoryBytes;
        set { if (_memoryBytes != value) { _memoryBytes = value; OnPropertyChanged(nameof(MemoryText)); } }
    }

    public ProcessStateKind State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(StateBrush)); } }
    }

    /// <summary>是否可操作（亮显）：false 时灰色显示且不可选中/操作。</summary>
    public bool IsEligible
    {
        get => _isEligible;
        set { if (_isEligible != value) { _isEligible = value; OnPropertyChanged(); } }
    }

    public string DisplayName => Name + ".exe";
    public string CpuText => $"{_cpuPercent:F1}%";
    public string MemoryText => FormatMemory(_memoryBytes);

    /// <summary>记忆规则提示（形如 "自动·冻结"）；空串表示该进程没有记忆设定。</summary>
    public string AutoText
    {
        get => _autoText;
        set { if (_autoText != value) { _autoText = value; OnPropertyChanged(); } }
    }

    public string StateText => _state switch
    {
        ProcessStateKind.BelowNormal => "低于正常",
        ProcessStateKind.BelowNormalTrimmed => "低于正常·虚拟内存",
        ProcessStateKind.Frozen => "冻结",
        _ => "正常",
    };

    /// <summary>状态行背景色（浅蓝 / 蓝 / 浅绿），取自主题资源，随主题切换自动更新。</summary>
    public Brush StateBrush => _state switch
    {
        ProcessStateKind.BelowNormal => GetThemeBrush("StateBelowNormalBrush"),
        ProcessStateKind.BelowNormalTrimmed => GetThemeBrush("StateTrimmedBrush"),
        ProcessStateKind.Frozen => GetThemeBrush("StateFrozenBrush"),
        _ => Brushes.Transparent,
    };

    /// <summary>主题切换后由 UI 调用，强制刷新状态底色。</summary>
    public void RefreshStateVisual() => OnPropertyChanged(nameof(StateBrush));

    private static Brush GetThemeBrush(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public static string FormatMemory(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        if (mb >= 1024) return $"{mb / 1024.0:F2} GB";
        return $"{mb:F1} MB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
