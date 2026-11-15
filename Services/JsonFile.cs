using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcPilot.Services;

/// <summary>
/// JSON 文件读写：容错加载 + 原子写入（写临时文件再替换，避免断电/崩溃造成半截文件）。
/// </summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读取；文件不存在/损坏时返回 null（损坏文件会被改名备份，便于排查）。</summary>
    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var text = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (Exception ex)
        {
            DiagLog.WriteException($"JsonFile.Load({System.IO.Path.GetFileName(path)})", ex);
            try
            {
                var bak = path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                System.IO.File.Move(path, bak, overwrite: true);
                DiagLog.Write($"损坏的配置文件已备份：{bak}");
            }
            catch { }
            return null;
        }
    }

    public static bool Save<T>(string path, T value) where T : class
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(value, Options);
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.WriteException($"JsonFile.Save({System.IO.Path.GetFileName(path)})", ex);
            return false;
        }
    }
}
