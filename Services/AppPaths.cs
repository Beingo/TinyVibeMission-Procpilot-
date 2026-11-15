using System.IO;

namespace ProcPilot.Services;

/// <summary>
/// 应用数据目录与文件名集中定义（默认 %APPDATA%\ProcPilot）。
/// 规则、设置、诊断日志都落在这里，卸载时删除该目录即可。
/// </summary>
public static class AppPaths
{
    private static string? _dataDir;

    /// <summary>%APPDATA%\ProcPilot</summary>
    public static string DataDir => _dataDir ??= EnsureDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RulesFile => Path.Combine(DataDir, "rules.json");
    public static string LogFile => Path.Combine(DataDir, "ProcPilot-diag.log");

    /// <summary>测试用：把数据目录改到临时位置，避免污染用户配置。</summary>
    public static void UseDataDir(string dir)
    {
        Directory.CreateDirectory(dir);
        _dataDir = dir;
    }

    private static string EnsureDataDir()
    {
        string dir;
        try
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcPilot");
            Directory.CreateDirectory(dir);
        }
        catch
        {
            dir = Path.GetTempPath();
        }
        return dir;
    }
}
