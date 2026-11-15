using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ProcPilot.Models;
using ProcPilot.Services;

namespace ProcPilot;

/// <summary>
/// 设置窗口：进程记忆规则管理、开机自启动（含随机延迟）、后台采样与诊断开关。
/// 所有改动即时生效并落盘（关闭窗口不需要额外确认）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly RuleStore _rules;
    private readonly AppSettings _settings;
    private readonly string _engineStatus;
    private readonly ObservableCollection<ProcessRule> _view = new();
    private bool _loading;

    public SettingsWindow(RuleStore rules, AppSettings settings, string engineStatus)
    {
        InitializeComponent();
        _rules = rules;
        _settings = settings;
        _engineStatus = engineStatus;

        LvRules.ItemsSource = _view;
        Loaded += (_, _) => LoadAll();
    }

    private void LoadAll()
    {
        _loading = true;
        try
        {
            ChkAutoRemember.IsChecked = _settings.AutoRemember;
            ChkApplyRunning.IsChecked = _settings.ApplyRulesToRunningAtStartup;
            ChkRandomDelay.IsChecked = _settings.RandomStartDelay;
            ChkStartHidden.IsChecked = _settings.StartHiddenOnAutoStart;
            ChkPauseUnfocused.IsChecked = _settings.PauseSamplingWhenUnfocused;
            ChkDiag.IsChecked = _settings.Diagnostics;
            TxtDelayMax.Text = _settings.RandomStartDelayMaxSec.ToString();

            // 注册表实际状态为准（可能被别的工具/手工改过）
            bool registered = AutoStartManager.IsEnabled();
            ChkAutoStart.IsChecked = registered || _settings.AutoStart;

            RefreshRules();
            UpdateInfoTexts();
            TxtDataDir.Text = "配置目录：" + AppPaths.DataDir;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshRules()
    {
        _view.Clear();
        foreach (var r in _rules.Snapshot()) _view.Add(r);
    }

    private void UpdateInfoTexts()
    {
        var cmd = AutoStartManager.RegisteredCommand();
        TxtAutoStartInfo.Text = string.IsNullOrEmpty(cmd)
            ? "当前未登记开机启动项。"
            : "当前启动项：" + cmd;

        TxtEngineInfo.Text =
            $"进程事件源：{_engineStatus}\n" +
            $"采样：前台 {_settings.ForegroundIntervalMs}ms / 未聚焦 {_settings.UnfocusedIntervalMs}ms / 后台隐藏时 0（完全停止）\n" +
            $"规则文件：{AppPaths.RulesFile}";
    }

    private void Persist()
    {
        if (_loading) return;
        SettingsStore.Save();
        UpdateInfoTexts();
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoRemember = ChkAutoRemember.IsChecked == true;
        _settings.ApplyRulesToRunningAtStartup = ChkApplyRunning.IsChecked == true;
        _settings.RandomStartDelay = ChkRandomDelay.IsChecked == true;
        _settings.StartHiddenOnAutoStart = ChkStartHidden.IsChecked == true;
        _settings.PauseSamplingWhenUnfocused = ChkPauseUnfocused.IsChecked == true;
        _settings.Diagnostics = ChkDiag.IsChecked == true;
        DiagLog.Enabled = _settings.Diagnostics;
        Persist();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = ChkAutoStart.IsChecked == true;
        var (ok, err) = AutoStartManager.SetEnabled(want);
        if (!ok)
        {
            _loading = true;
            ChkAutoStart.IsChecked = AutoStartManager.IsEnabled();
            _loading = false;
            MessageBox.Show(this, "设置开机启动失败：" + err, "ProcPilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.AutoStart = want;
        Persist();
    }

    private void ApplyDelayMax()
    {
        if (int.TryParse(TxtDelayMax.Text.Trim(), out int v))
        {
            _settings.RandomStartDelayMaxSec = Math.Clamp(v, 1, 300);
        }
        TxtDelayMax.Text = _settings.RandomStartDelayMaxSec.ToString();
        SettingsStore.Save();
        UpdateInfoTexts();
    }

    private void OnDelayMaxChanged(object sender, RoutedEventArgs e) => ApplyDelayMax();

    private void OnDelayMaxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyDelayMax();
            Keyboard.ClearFocus();
        }
    }

    private void OnToggleRule(object sender, RoutedEventArgs e)
    {
        if (LvRules.SelectedItem is not ProcessRule rule) return;
        _rules.SetEnabled(rule.Name, !rule.Enabled);
        RefreshRules();
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (LvRules.SelectedItem is not ProcessRule rule) return;
        _rules.Remove(rule.Name);
        RefreshRules();
    }

    private void OnClearRules(object sender, RoutedEventArgs e)
    {
        if (_rules.Count == 0) return;
        var result = MessageBox.Show(this, $"确定要清空全部 {_rules.Count} 条进程记忆吗？", "ProcPilot",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _rules.RemoveAll();
        RefreshRules();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow(_engineStatus) { Owner = this };
        window.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
