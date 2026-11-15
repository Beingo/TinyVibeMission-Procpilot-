using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ProcPilot.Services;

/// <summary>
/// 程序信息（版本 / 运行环境 / 代码签名状态），供「关于」页面与诊断信息使用。
/// 全部在运行时读取，不写死任何机器名或本地路径。
/// </summary>
public static class AppInfo
{
    /// <summary>产品名。</summary>
    public const string ProductName = "ProcPilot";

    /// <summary>一句话简介。</summary>
    public const string Tagline = "Windows 进程优先级 / 冻结管理器 —— 记住每个进程的设定，下次启动自动应用";

    /// <summary>程序版本（来自程序集）。</summary>
    public static string Version
    {
        get
        {
            try
            {
                var asm = typeof(AppInfo).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
                return asm.GetName().Version?.ToString(3) ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }
    }

    /// <summary>构建时间（来自程序集文件版本信息；单文件发布时取 exe 的写盘时间范围内的构建戳）。</summary>
    public static string BuildTime
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            return "未知";
        }
    }

    /// <summary>.NET 运行时版本。</summary>
    public static string RuntimeVersion => Environment.Version.ToString();

    /// <summary>操作系统描述。</summary>
    public static string OsDescription => Environment.OSVersion.VersionString;

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>可执行文件路径（运行时值，不参与展示以外的用途）。</summary>
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
            return "";
        }
    }

    // ---------------- 代码签名 ----------------

    public sealed class SignatureStatus
    {
        public bool Signed { get; init; }
        public string Subject { get; init; } = "";
        public string Issuer { get; init; } = "";
        public bool Trusted { get; init; }
        public string Detail { get; init; } = "";

        public string Summary => !Signed
            ? "未签名"
            : Trusted
                ? "已签名（证书链在本机受信任）"
                : "已签名（证书不受本机信任）";
    }

    /// <summary>
    /// 读取 Authenticode 签名信息并校验证书链。
    /// 自签名证书需要先导入"受信任的根证书颁发机构"，否则 <see cref="SignatureStatus.Trusted"/> 为 false。
    /// </summary>
    public static SignatureStatus InspectSignature()
    {
        var exe = ExecutablePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return new SignatureStatus { Signed = false, Detail = "取不到可执行文件路径" };

        try
        {
#pragma warning disable SYSLIB0026 // X509Certificate.CreateFromSignedFile：仅用于读取 Authenticode 签名，不涉及证书校验逻辑
            var raw = X509Certificate.CreateFromSignedFile(exe);
#pragma warning restore SYSLIB0026
            using var cert = new X509Certificate2(raw);
            bool trusted;
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                trusted = chain.Build(cert);
            }
            return new SignatureStatus
            {
                Signed = true,
                Subject = cert.Subject,
                Issuer = cert.Issuer,
                Trusted = trusted,
                Detail = trusted
                    ? "证书链校验通过"
                    : "证书链不受信任：自签名证书需导入到「受信任的根证书颁发机构」后才能验证通过",
            };
        }
        catch (CryptographicException)
        {
            return new SignatureStatus { Signed = false, Detail = "文件没有 Authenticode 签名" };
        }
        catch (Exception ex)
        {
            return new SignatureStatus { Signed = false, Detail = ex.Message };
        }
    }

    /// <summary>拼一段可直接粘贴给他人排查的文本（不含个人路径以外的隐私内容）。</summary>
    public static string BuildDiagnosticsText(string engineStatus)
    {
        var sig = InspectSignature();
        return string.Join(Environment.NewLine,
            $"{ProductName} {Version}（构建 {BuildTime}）",
            $"运行环境：.NET {RuntimeVersion} / {OsDescription}",
            $"管理员权限：{(IsElevated ? "是" : "否")}",
            $"进程事件源：{engineStatus}",
            $"代码签名：{sig.Summary}",
            sig.Signed ? $"签名主体：{sig.Subject}" : "签名主体：-",
            $"配置文件目录：{AppPaths.DataDir}");
    }
}
