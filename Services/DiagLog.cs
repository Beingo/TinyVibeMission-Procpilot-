using System.IO;
using System.Text;

namespace ProcPilot.Services;

/// <summary>
/// 轻量文件日志（%APPDATA%\ProcPilot\ProcPilot-diag.log）。
///
/// 相比旧版（每次 File.AppendAllText 打开/关闭文件）：
/// - 默认【关闭】常规采样日志，只在设置里打开 Diagnostics 或设置环境变量 PROCIPILOT_DIAG=1 时记录，
///   避免常驻软件在后台反复做文件 IO（这是需求3"降低后台资源消耗"的一部分）；
/// - 打开时使用长驻 StreamWriter + 批量写，单次写不再 open/close；
/// - 文件超过 2MB 自动轮转一次（ProcPilot-diag.1.log），不会无限增长。
/// </summary>
public static class DiagLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static bool _initialized;

    private static bool _enabled;
    private static bool _forced;

    /// <summary>常规诊断日志开关（异常与启动信息不受此开关限制，始终记录）。</summary>
    public static bool Enabled
    {
        get => _enabled || _forced;
        set => _enabled = value && !_forced;
    }

    private static string? _overridePath;

    /// <summary>测试用：改写日志路径（自测程序不污染用户目录）。</summary>
    public static void UseFile(string path)
    {
        lock (Lock)
        {
            _overridePath = path;
            _writer?.Dispose();
            _writer = null;
            _initialized = false;
        }
    }

    private static string LogPath => _overridePath ?? AppPaths.LogFile;

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _forced = Environment.GetEnvironmentVariable("PROCIPILOT_DIAG") == "1";
        }
        catch { }

        try
        {
            var path = LogPath;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var old = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(path) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(path) + ".1.log");
                File.Move(path, old, overwrite: true);
            }
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ProcPilot start (pid={Environment.ProcessId})");
        }
        catch
        {
            _writer = null;
        }
    }

    /// <summary>写一条日志（受 Enabled 开关控制）。</summary>
    public static void Write(string msg)
    {
        lock (Lock)
        {
            Init();
            if (!Enabled) return;
            try { _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { }
        }
    }

    /// <summary>异常始终记录（数量少、价值高）。</summary>
    public static void WriteException(string tag, Exception ex)
    {
        lock (Lock)
        {
            Init();
            try
            {
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
            catch { }
        }
    }

    /// <summary>强制落盘（退出前调用）。</summary>
    public static void Flush()
    {
        lock (Lock)
        {
            try { _writer?.Flush(); } catch { }
        }
    }
}
