using ProcPilot.Models;

namespace ProcPilot.Services;

/// <summary>
/// 进程记忆规则仓库（线程安全）：
/// - 按进程名（忽略大小写、去掉 .exe）索引；
/// - 任何改动立即原子写盘（rules.json 很小，几百条也就几十 KB）；
/// - 供自动应用引擎与 UI 共用。
/// </summary>
public sealed class RuleStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ProcessRule> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>规则变化（增删改）后触发；线程池回调。</summary>
    public event Action? Changed;

    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    public void Load()
    {
        var doc = JsonFile.Load<RuleDocument>(AppPaths.RulesFile);
        lock (_lock)
        {
            _byName.Clear();
            if (doc?.Rules == null) return;
            foreach (var r in doc.Rules)
            {
                var key = NormalizeName(r.Name);
                if (key.Length == 0) continue;
                if (r.State == ProcessStateKind.Normal) continue; // 正常状态不需要记忆
                r.Name = key;
                _byName[key] = r;
            }
        }
    }

    public void Save()
    {
        RuleDocument doc;
        lock (_lock)
        {
            doc = new RuleDocument
            {
                Rules = _byName.Values
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => r.Clone())
                    .ToList(),
            };
        }
        JsonFile.Save(AppPaths.RulesFile, doc);
    }

    public bool TryGet(string name, out ProcessRule rule)
    {
        var key = NormalizeName(name);
        lock (_lock)
        {
            if (key.Length > 0 && _byName.TryGetValue(key, out var found))
            {
                rule = found.Clone();
                return true;
            }
        }
        rule = new ProcessRule();
        return false;
    }

    /// <summary>新增/更新规则。state 为 Normal 时等价于删除。</summary>
    public void Upsert(string name, ProcessStateKind state, string? imagePath = null, bool enabled = true, int delayMs = 0)
    {
        var key = NormalizeName(name);
        if (key.Length == 0) return;
        bool changed = false;
        lock (_lock)
        {
            if (state == ProcessStateKind.Normal)
            {
                if (_byName.Remove(key)) changed = true;
            }
            else
            {
                if (!_byName.TryGetValue(key, out var rule))
                {
                    rule = new ProcessRule { Name = key };
                    _byName[key] = rule;
                }
                if (rule.State != state || rule.Enabled != enabled || rule.DelayMs != delayMs)
                {
                    rule.State = state;
                    rule.Enabled = enabled;
                    rule.DelayMs = delayMs;
                    rule.UpdatedAt = DateTime.Now;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(imagePath)) rule.ImagePath = imagePath;
                if (!changed) rule.UpdatedAt = DateTime.Now; // 重复设置同一状态也算"最近使用"
            }
        }
        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
    }

    public bool Remove(string name)
    {
        var key = NormalizeName(name);
        bool removed;
        lock (_lock) removed = _byName.Remove(key);
        if (removed)
        {
            Save();
            Changed?.Invoke();
        }
        return removed;
    }

    public int RemoveAll()
    {
        int n;
        lock (_lock)
        {
            n = _byName.Count;
            _byName.Clear();
        }
        if (n > 0)
        {
            Save();
            Changed?.Invoke();
        }
        return n;
    }

    public void SetEnabled(string name, bool enabled)
    {
        var key = NormalizeName(name);
        lock (_lock)
        {
            if (!_byName.TryGetValue(key, out var rule)) return;
            rule.Enabled = enabled;
            rule.UpdatedAt = DateTime.Now;
        }
        Save();
        Changed?.Invoke();
    }

    public List<ProcessRule> Snapshot()
    {
        lock (_lock)
            return _byName.Values
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Clone())
                .ToList();
    }

    public int Count
    {
        get { lock (_lock) return _byName.Count; }
    }

    public sealed class RuleDocument
    {
        public int Version { get; set; } = 1;
        public List<ProcessRule> Rules { get; set; } = new();
    }
}
