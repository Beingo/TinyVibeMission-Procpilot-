using ProcPilot.Models;

namespace ProcPilot.Services;

/// <summary>设置仓库：全局单例 Current，改动后 Save() 落盘。</summary>
public static class SettingsStore
{
    private static AppSettings? _current;
    private static readonly object Lock = new();

    /// <summary>当前设置（首次访问时从磁盘加载）。</summary>
    public static AppSettings Current
    {
        get
        {
            lock (Lock)
            {
                if (_current == null)
                {
                    _current = JsonFile.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
                    _current.Normalize();
                }
                return _current;
            }
        }
    }

    public static bool Save()
    {
        lock (Lock)
        {
            Current.Normalize();
            return JsonFile.Save(AppPaths.SettingsFile, Current);
        }
    }
}
