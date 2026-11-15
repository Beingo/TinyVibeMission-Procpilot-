using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ProcPilot.Services;

namespace ProcPilot;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获：记录日志；UI 线程异常标记已处理避免闪退，后台线程异常仅记录
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DiagLog.WriteException("AppDomain.UnhandledException", ex ?? new Exception("unknown"));
        };
        DispatcherUnhandledException += (_, args) =>
        {
            DiagLog.WriteException("DispatcherUnhandledException", args.Exception);
            try
            {
                MessageBox.Show($"界面发生未处理的异常：\n{args.Exception.Message}", "ProcPilot",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true; // 阻止闪退，记录后继续运行
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog.WriteException("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // 单实例：避免开机自启 + 手动启动同时跑两份（两份会同时应用规则）
        _singleInstance = new Mutex(initiallyOwned: true, "ProcPilot-SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("ProcPilot 已经在运行（请查看系统托盘图标）。", "ProcPilot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = SettingsStore.Current;
        DiagLog.Enabled = settings.Diagnostics;

        // 跟随系统深浅色主题
        ThemeManager.Initialize();
        ThemeManager.ApplyTheme();
        ThemeManager.ThemeChanged += _ => ThemeManager.ApplyTheme();

        // 需求2：开机自启动 + 随机延迟
        bool autoStart = AutoStartManager.IsAutoStartLaunch(e.Args);
        bool startHidden = autoStart && settings.StartHiddenOnAutoStart;
        int delaySeconds = 0;
        if (autoStart && settings.RandomStartDelay)
        {
            int max = Math.Max(1, settings.RandomStartDelayMaxSec);
            delaySeconds = Random.Shared.Next(1, max + 1); // 1..max 秒
        }
        DiagLog.Write($"启动：autoStart={autoStart} hidden={startHidden} delay={delaySeconds}s");

        MainWindow = new MainWindow(startHidden, delaySeconds);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        DiagLog.Flush();
        base.OnExit(e);
    }
}
