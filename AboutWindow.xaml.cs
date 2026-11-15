using System.Windows;
using ProcPilot.Services;

namespace ProcPilot;

/// <summary>
/// 「关于」页面：版本、运行环境、代码签名状态、设计要点。
/// 这些信息全部在运行时读取，页面本身不写死任何机器名或本地路径。
/// </summary>
public partial class AboutWindow : Window
{
    private readonly string _engineStatus;

    public AboutWindow(string engineStatus)
    {
        InitializeComponent();
        _engineStatus = engineStatus;

        AppIcon.Source = IconProvider.ToImageSource(IconProvider.GetGearIcon());
        TxtTitle.Text = $"{AppInfo.ProductName} {AppInfo.Version}";
        TxtTagline.Text = AppInfo.Tagline;

        var sig = AppInfo.InspectSignature();
        TxtInfo.Text = string.Join(Environment.NewLine, new[]
        {
            $"版本：{AppInfo.Version}（构建 {AppInfo.BuildTime}）",
            $"运行环境：.NET {AppInfo.RuntimeVersion} · {AppInfo.OsDescription}",
            $"权限：{(AppInfo.IsElevated ? "管理员" : "普通用户（只能操作自己的进程）")}",
            $"进程事件源：{_engineStatus}",
            $"代码签名：{sig.Summary}",
            sig.Signed ? $"签名主体：{sig.Subject}" : "签名主体：—（未签名不影响使用；对外分发可自行用正式证书签名）",
            $"配置目录：{AppPaths.DataDir}",
            "许可证：MIT（允许商用，请保留版权与许可声明）",
        });

        TxtDesign.Text = string.Join(Environment.NewLine, new[]
        {
            "· 采样：一次 NtQuerySystemInformation 取回全部进程的名字/会话/工作集/CPU 时间，",
            "  不再逐进程打开句柄；采样强度分档（前台 1s / 未聚焦 2s / 托盘隐藏 0）。",
            "· 事件：进程启动用 ETW 内核进程事件（系统推送）驱动，后台零轮询；",
            "  ETW 不可用时自动降级为 2s 一次的轻量轮询。",
            "· 规则：用户设定写入 rules.json（按进程名匹配），进程启动即自动应用，",
            "  并在 2.5s 后复核一次，防止程序自己把优先级改回去。",
            "· 展示：可操作进程优先排序，UI 虚拟化 + 只在排序真变化时重排；",
            "  低占用进程灰色不可操作，避免误操作。",
            "· 安全：需要管理员权限才能修改他人进程；退出时统一恢复所有受控进程。",
        });
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开目录失败：" + ex.Message, "ProcPilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyInfo(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppInfo.BuildDiagnosticsText(_engineStatus));
            MessageBox.Show(this, "已复制运行信息到剪贴板。", "ProcPilot",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "ProcPilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
