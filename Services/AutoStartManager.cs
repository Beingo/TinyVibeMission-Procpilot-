using Microsoft.Win32;

namespace ProcPilot.Services;

/// <summary>
/// 开机自启动管理：写入当前用户 Run 键（HKCU\...\CurrentVersion\Run），不需要管理员权限。
/// 单文件 exe 里 Environment.ProcessPath 就是 exe 自身路径。
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProcPilot";
    private const string AutoStartArg = "--autostart";

    public static string ExecutablePath
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        }
    }

    /// <summary>命令行是否由"开机自启动"带入。</summary>
    public static bool IsAutoStartLaunch(IEnumerable<string>? args)
        => args?.Any(a => string.Equals(a, AutoStartArg, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("AutoStartManager.IsEnabled", ex);
            return false;
        }
    }

    /// <summary>开启/关闭自启动。返回 (成功, 错误信息)。</summary>
    public static (bool Ok, string Error) SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return (false, "无法打开注册表 Run 键");
            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe)) return (false, "无法确定当前程序路径");
                key.SetValue(ValueName, $"\"{exe}\" {AutoStartArg}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("AutoStartManager.SetEnabled", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>读取注册表里实际登记的启动命令（用于界面展示）。返回 null 表示未登记。</summary>
    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
