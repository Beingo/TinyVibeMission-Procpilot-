using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProcPilot.Models;
using ProcPilot.Services;
using WinForms = System.Windows.Forms;

namespace ProcPilot;

public partial class MainWindow : Window
{
    private readonly ProcessController _controller = new();
    private readonly ProcessMonitor _monitor;
    private readonly RuleStore _rules = new();
    private readonly AppSettings _settings;
    private RuleEngine? _engine;
    private readonly Dictionary<int, ProcessInfo> _items = new();
    private DateTime _lastUpdateAt = DateTime.UtcNow;
    private long _lastOrderSignature;
    private WinForms.NotifyIcon? _trayIcon;
    private DispatcherTimer? _engineStartTimer;
    private bool _allowClose;
    private bool _exiting;

    public ObservableCollection<ProcessInfo> Processes { get; } = new();

    /// <summary>事件源/规则状态说明（供设置窗口展示）。</summary>
    public string EngineStatus => _engine?.Status ?? "未启动";

    public MainWindow(bool startHidden = false, int delayedEngineStartSeconds = 0)
    {
        InitializeComponent();

        _settings = SettingsStore.Current;
        DiagLog.Enabled = _settings.Diagnostics;
        _rules.Load();
        _monitor = new ProcessMonitor(_controller);
        _monitor.SetIntervals(_settings.ForegroundIntervalMs, _settings.UnfocusedIntervalMs);

        // Windows 自带齿轮图标（窗口/任务栏 + 系统托盘）
        Icon = IconProvider.ToImageSource(IconProvider.GetGearIcon());
        InitTray();

        ProcessList.ItemsSource = Processes;

        // 排序：可操作进程永远在灰色进程之上；组内按内存占用从大到小
        var view = CollectionViewSource.GetDefaultView(Processes);
        if (view is ListCollectionView lcv)
        {
            lcv.CustomSort = Comparer<object>.Create((a, b) =>
            {
                var pa = (ProcessInfo)a;
                var pb = (ProcessInfo)b;
                int byEligible = pb.IsEligible.CompareTo(pa.IsEligible);
                return byEligible != 0 ? byEligible : pb.MemoryBytes.CompareTo(pa.MemoryBytes);
            });
        }

        _monitor.SnapshotUpdated += OnSnapshotReady;
        _controller.StateChanged += OnProcessStateChanged;
        _rules.Changed += () => Dispatcher.BeginInvoke(() => { RefreshAutoText(); UpdateFooter(); });
        ThemeManager.ThemeChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in _items.Values) item.RefreshStateVisual();
            UpdateStateButtons(ProcessList.SelectedItem as ProcessInfo);
        });

        Activated += (_, _) => ApplySampleModeByFocus();
        Deactivated += (_, _) => ApplySampleModeByFocus();
        IsVisibleChanged += (_, _) => ApplySampleModeByFocus();
        Loaded += (_, _) =>
        {
            RefreshAutoText();
            UpdateFooter();
            if (startHidden)
            {
                Hide(); // 开机自启：静默驻留托盘
                StatusText.Text = "已在托盘后台驻留";
            }
            else
            {
                ApplySampleModeByFocus();
                StatusText.Text = "正在采样…";
            }
        };
        Closed += (_, _) => Shutdown();

        UpdateStateButtons(null);
        StartEngine(delayedEngineStartSeconds);
    }

    private void StartEngine(int delayedSeconds)
    {
        if (delayedSeconds <= 0)
        {
            StartEngineNow();
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"随机延迟启动：{delayedSeconds}s 后开始自动规则";
            _engineStartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delayedSeconds) };
            _engineStartTimer.Tick += (_, _) =>
            {
                _engineStartTimer?.Stop();
                _engineStartTimer = null;
                StartEngineNow();
            };
            _engineStartTimer.Start();
        });
    }

    private void StartEngineNow()
    {
        if (_engine != null || _exiting) return;
        _engine = new RuleEngine(_controller, _rules, _settings);
        _engine.AutoApplied += (pid, state, name) => Dispatcher.BeginInvoke(() =>
        {
            if (_items.TryGetValue(pid, out var item)) item.State = state;
            _monitor.SetPinnedPids(_controller.GetControlledPids());
            StatusText.Text = $"已自动应用规则：{name}.exe → {StateName(state)}";
            UpdateFooter();
        });
        _engine.Start();
        _monitor.SetPinnedPids(_controller.GetControlledPids());
        StatusText.Text = $"自动规则已启用 · {_engine.Status}";
        UpdateFooter();
    }

    // ---------------- 采样强度（前台 / 未聚焦 / 后台） ----------------

    /// <summary>
    /// 前台：正常采样；窗口可见但未聚焦：慢采样（可配置为暂停）；窗口隐藏到托盘：完全停止采样。
    /// 这样"后台"时本进程不做任何轮询，只有 ETW 事件回调（需求3/4）。
    /// </summary>
    private void ApplySampleModeByFocus()
    {
        if (!IsVisible)
        {
            _monitor.SetMode(SampleMode.Off);
        }
        else if (IsActive)
        {
            _monitor.SetMode(SampleMode.Normal);
        }
        else
        {
            _monitor.SetMode(_settings.PauseSamplingWhenUnfocused ? SampleMode.Off : SampleMode.Slow);
        }
        UpdateFooter();
    }

    // ---------------- 托盘：关闭最小化 + 右键退出/显示 ----------------

    private void InitTray()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = IconProvider.GetGearIcon(),
            Text = "ProcPilot - 进程管理器",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        menu.Items.Add("关于 ProcPilot…", null, (_, _) => Dispatcher.BeginInvoke(OpenAbout));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitApp));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        Closing += OnWindowClosing;
    }

    /// <summary>点关闭按钮：不退出，最小化到系统托盘。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        StatusText.Text = "已最小化到托盘（右键托盘图标可退出或显示主界面）";
        UpdateFooter();
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ApplySampleModeByFocus();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _allowClose = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private void Shutdown()
    {
        _engineStartTimer?.Stop();
        _engineStartTimer = null;
        try { _engine?.Dispose(); } catch { }
        _monitor.SetMode(SampleMode.Off);
        _monitor.Dispose();
        // 退出前统一恢复所有被改进程，避免残留
        _controller.RestoreAll();
        DiagLog.Flush();
    }

    // ---------------- 采样 / 展示 ----------------

    private void OnSnapshotReady(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try { ApplySnapshot(snapshots); }
                catch (Exception ex) { DiagLog.WriteException("MainWindow.ApplySnapshot", ex); }
            });
        }
        catch (Exception ex)
        {
            // 窗口关闭竞态等：记录但绝不崩溃
            DiagLog.WriteException("MainWindow.BeginInvoke", ex);
        }
    }

    private void ApplySnapshot(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        try
        {
            if (_monitor.Mode == SampleMode.Off) return; // 已切到后台，丢弃在途快照

            var existing = new HashSet<int>(_items.Keys);
            foreach (var s in snapshots)
            {
                if (_items.TryGetValue(s.Pid, out var item))
                {
                    item.CpuPercent = s.CpuPercent;
                    item.MemoryBytes = s.WorkingSet64;
                    item.IsEligible = s.IsEligible;
                    item.State = s.State;
                }
                else
                {
                    item = new ProcessInfo
                    {
                        Pid = s.Pid,
                        Name = s.Name,
                        CpuPercent = s.CpuPercent,
                        MemoryBytes = s.WorkingSet64,
                        State = s.State,
                        IsEligible = s.IsEligible,
                        AutoText = AutoTextFor(s.Name),
                    };
                    _items[s.Pid] = item;
                    Processes.Add(item);
                }
                existing.Remove(s.Pid);
            }

            foreach (var pid in existing)
            {
                if (_items.TryGetValue(pid, out var gone))
                {
                    Processes.Remove(gone);
                    _items.Remove(pid);
                }
            }

            // 一致性自愈：集合与字典数量不一致时重建，杜绝重复/缺失
            if (Processes.Count != _items.Count)
            {
                DiagLog.Write($"ApplySnapshot desync repaired: Processes={Processes.Count} _items={_items.Count}");
                Processes.Clear();
                foreach (var kv in _items) Processes.Add(kv.Value);
            }

            // 只有"排序结果真的会变"时才重排：每秒无条件 Refresh 会让 ListView 整表重排重绘。
            // 内存按 4MB 分桶后比较：内存只是在几 MB 内抖动时完全不用重排。
            long sig = 17;
            foreach (var s in snapshots)
                sig = unchecked(sig * 31 + s.Pid + (s.IsEligible ? 0x1000000L : 0) + (s.WorkingSet64 >> 22));
            if (sig != _lastOrderSignature)
            {
                _lastOrderSignature = sig;
                (CollectionViewSource.GetDefaultView(Processes) as ListCollectionView)?.Refresh();
            }
            _lastUpdateAt = DateTime.UtcNow;

            // 选中的进程变为灰色（不可操作）时，取消选中状态
            var sel = ProcessList.SelectedItem as ProcessInfo;
            if (sel != null && !sel.IsEligible)
            {
                ProcessList.SelectedItem = null;
            }

            StatusText.Text = _monitor.IsWarmup
                ? $"预热中：展示全部非系统进程（{snapshots.Count} 个）…"
                : $"正在采样 · 展示 {snapshots.Count} 个进程（可操作 {snapshots.Count(s => s.IsEligible)}）";

            UpdateFooter(snapshots.Count);
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("MainWindow.ApplySnapshot", ex);
        }
    }

    private void UpdateFooter(int visibleCount = -1)
    {
        if (FooterText == null) return;
        var mode = _monitor.Mode switch
        {
            SampleMode.Off => "暂停采样（后台）",
            SampleMode.Slow => $"慢采样 {_settings.UnfocusedIntervalMs}ms",
            _ => $"采样 {_settings.ForegroundIntervalMs}ms",
        };
        var count = visibleCount >= 0 ? visibleCount.ToString() : _items.Count.ToString();
        FooterText.Text = $"事件源：{EngineStatus} · 记忆规则 {_rules.Count} 条 · {mode} · " +
                          $"采样耗时 {_monitor.LastTickMicros}µs · 列表 {count} 个进程";
    }

    private string AutoTextFor(string name)
    {
        if (!_rules.TryGet(name, out var rule) || !rule.Enabled) return "";
        return "自动·" + rule.StateText;
    }

    private void RefreshAutoText()
    {
        foreach (var item in _items.Values) item.AutoText = AutoTextFor(item.Name);
    }

    private void OnProcessStateChanged(int pid, ProcessStateKind kind)
    {
        // 自动规则引擎会在后台线程应用状态，必须回到 UI 线程更新
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnProcessStateChanged(pid, kind));
            return;
        }
        if (_items.TryGetValue(pid, out var item)) item.State = kind;
        _monitor.SetPinnedPids(_controller.GetControlledPids());
        UpdateStateButtons(ProcessList.SelectedItem as ProcessInfo);
        UpdateFooter();
    }

    // ---------------- 选中 / 状态按钮联动 ----------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStateButtons(ProcessList.SelectedItem as ProcessInfo);
    }

    /// <summary>工具栏按钮高亮反映选中进程当前状态。</summary>
    private void UpdateStateButtons(ProcessInfo? item)
    {
        var kind = item?.State ?? ProcessStateKind.Normal;
        SetButtonActive(BtnBelowNormal, kind == ProcessStateKind.BelowNormal);
        SetButtonActive(BtnTrimmed, kind == ProcessStateKind.BelowNormalTrimmed);
        SetButtonActive(BtnFrozen, kind == ProcessStateKind.Frozen);
        SetButtonActive(BtnNormal, kind == ProcessStateKind.Normal);
    }

    private static void SetButtonActive(Button btn, bool active)
    {
        btn.BorderThickness = active ? new Thickness(2) : new Thickness(0);
        btn.BorderBrush = active
            ? (System.Windows.Application.Current?.TryFindResource("TextPrimaryBrush") as Brush) ?? Brushes.Black
            : Brushes.Transparent;
        btn.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
    }

    // ---------------- 右键菜单 ----------------

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 右键优先选中目标行
        if (e.OriginalSource is DependencyObject source)
        {
            var item = FindAncestor<ListViewItem>(source);
            if (item != null && item.DataContext is ProcessInfo)
            {
                item.IsSelected = true;
            }
        }

        // 菜单勾选指示当前选中进程状态
        var selected = ProcessList.SelectedItem as ProcessInfo;
        var kind = selected?.State ?? ProcessStateKind.Normal;
        MenuBelowNormal.IsChecked = kind == ProcessStateKind.BelowNormal;
        MenuTrimmed.IsChecked = kind == ProcessStateKind.BelowNormalTrimmed;
        MenuFrozen.IsChecked = kind == ProcessStateKind.Frozen;
        MenuNormal.IsChecked = kind == ProcessStateKind.Normal;

        bool hasRule = selected != null && _rules.TryGet(selected.Name, out _);
        MenuRemember.IsEnabled = selected != null;
        MenuForget.IsEnabled = hasRule;
    }

    private void OnMenuStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            ApplyStateToSelected(Enum.Parse<ProcessStateKind>(tag));
        }
    }

    private void OnMenuRememberClick(object sender, RoutedEventArgs e) => RememberSelected();

    private void OnMenuForgetClick(object sender, RoutedEventArgs e)
    {
        var selected = ProcessList.SelectedItem as ProcessInfo;
        if (selected == null) return;
        if (_rules.Remove(selected.Name))
        {
            selected.AutoText = "";
            StatusText.Text = $"已忘记 {selected.Name}.exe 的设定";
        }
    }

    private void OnMenuSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnAboutMenuClick(object sender, RoutedEventArgs e) => OpenAbout();

    private void OnAboutButtonClick(object sender, RoutedEventArgs e) => OpenAbout();

    // ---------------- 状态切换 ----------------

    private void OnStateButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            ApplyStateToSelected(Enum.Parse<ProcessStateKind>(tag));
        }
    }

    private void OnRememberButtonClick(object sender, RoutedEventArgs e) => RememberSelected();

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void ApplyStateToSelected(ProcessStateKind kind)
    {
        var selected = ProcessList.SelectedItem as ProcessInfo;
        if (selected == null)
        {
            MessageBox.Show(this, "请先用鼠标左键选中一个进程。", "ProcPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!selected.IsEligible)
        {
            MessageBox.Show(this, "该进程当前不可操作：低占用进程（灰色显示）需持续占用 CPU 超过 1% 后方可操作。",
                "ProcPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, err) = _controller.ApplyState(selected.Pid, kind);
        if (!ok)
        {
            MessageBox.Show(this, err, "ProcPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 需求1：用户选择后记住，下次该进程启动自动应用
        if (_settings.AutoRemember)
        {
            if (kind == ProcessStateKind.Normal) _rules.Remove(selected.Name);
            else _rules.Upsert(selected.Name, kind, NativeMethods.TryGetImagePath(selected.Pid));
            selected.AutoText = AutoTextFor(selected.Name);
            StatusText.Text = kind == ProcessStateKind.Normal
                ? $"{selected.Name}.exe 已恢复正常，并取消了记忆设定"
                : $"{selected.Name}.exe 已设为 {StateName(kind)}，下次启动自动应用";
        }
        UpdateFooter();
    }

    /// <summary>"记住 → 自动化"按钮：把选中进程当前状态固化为规则。</summary>
    private void RememberSelected()
    {
        var selected = ProcessList.SelectedItem as ProcessInfo;
        if (selected == null)
        {
            MessageBox.Show(this, "请先选中一个进程。", "ProcPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.State == ProcessStateKind.Normal)
        {
            MessageBox.Show(this, "该进程当前是「正常」状态，没有可记忆的设定。\n先设置优先级/冻结，再点「记住」。",
                "ProcPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rules.Upsert(selected.Name, selected.State, NativeMethods.TryGetImagePath(selected.Pid));
        selected.AutoText = AutoTextFor(selected.Name);
        StatusText.Text = $"已记住：{selected.Name}.exe → {StateName(selected.State)}（下次启动自动应用）";
        UpdateFooter();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_rules, _settings, EngineStatus) { Owner = this };
        window.ShowDialog();
        _monitor.SetIntervals(_settings.ForegroundIntervalMs, _settings.UnfocusedIntervalMs);
        DiagLog.Enabled = _settings.Diagnostics;
        RefreshAutoText();
        ApplySampleModeByFocus();
        UpdateFooter();
    }

    /// <summary>「关于」页面：版本、运行环境、签名状态、设计要点。</summary>
    private void OpenAbout()
    {
        var window = new AboutWindow(EngineStatus) { Owner = this };
        window.ShowDialog();
    }

    private static string StateName(ProcessStateKind kind) => kind switch
    {
        ProcessStateKind.BelowNormal => "低于正常",
        ProcessStateKind.BelowNormalTrimmed => "低于正常·虚拟内存",
        ProcessStateKind.Frozen => "冻结",
        _ => "正常",
    };

    // ---------------- 工具 ----------------

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
