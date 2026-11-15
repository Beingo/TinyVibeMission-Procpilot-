using System.Windows;
using Microsoft.Win32;

namespace ProcPilot.Services;

/// <summary>
/// 跟随 Windows 系统深浅色主题：读取注册表 AppsUseLightTheme，
/// 并监听 SystemEvents.UserPreferenceChanged 实现运行时自动切换。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    /// <summary>系统深浅色切换后触发。</summary>
    public static event Action<bool>? ThemeChanged;

    /// <summary>启动时调用一次：检测当前主题并挂监听。</summary>
    public static void Initialize()
    {
        IsDark = DetectDark();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            var dark = DetectDark();
            if (dark != IsDark)
            {
                // SystemEvents 在后台线程触发：marshal 到 UI 线程再更新 WPF 资源
                var app = Application.Current;
                if (app == null)
                {
                    IsDark = dark;
                    ThemeChanged?.Invoke(dark);
                    return;
                }
                try
                {
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        IsDark = dark;
                        ThemeChanged?.Invoke(dark);
                    });
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("ThemeManager.BeginInvoke", ex);
                }
            }
        }
    }

    private static bool DetectDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch
        {
            return false; // 读取失败按浅色处理
        }
    }

    /// <summary>切换 App 全局资源中的主题颜色字典（DynamicResource 即时生效）。</summary>
    public static void ApplyTheme()
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged == null) return;

        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString;
            if (src != null && src.StartsWith("/Themes/Colors.", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = new ResourceDictionary
                {
                    Source = new Uri(IsDark ? "/Themes/Colors.Dark.xaml" : "/Themes/Colors.Light.xaml", UriKind.Relative),
                };
                return;
            }
        }
    }
}
