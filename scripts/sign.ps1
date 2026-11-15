#requires -Version 5.1
<#
.SYNOPSIS
    给 ProcPilot.exe 打 Authenticode 签名。支持正式证书（PFX / 证书存储）与仅本机测试用的自签名证书。

.DESCRIPTION
    证书来源三选一：
      -Pfx <file.pfx> -PfxPassword <pwd>   用 PFX 里的证书（正式证书常见交付形式）
      -Thumbprint <thumb>                  用本机证书存储里的证书（CurrentUser\My 或 LocalMachine\My）
      -SelfSigned                          生成/复用一张自签名证书（仅本机开发测试）

    重要说明：
      * 自签名证书只影响本机（UAC 里显示发布者名字），对 SmartScreen 与别人的电脑没有作用；
      * 想消除别人机器上的 SmartScreen 提示，需要正式代码签名证书（OV/EV，或开源项目的免费签名计划），
        并且新文件首发仍可能被拦一次（信誉需要积累）；
      * 时间戳：正式证书建议打（证书过期后签名仍有效）；离线环境用 -NoTimestamp。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Pfx .\mycert.pfx -PfxPassword '***'

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Thumbprint ABCDEF0123456789...

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -SelfSigned -TrustLocal
#>
[CmdletBinding(DefaultParameterSetName = 'SelfSigned')]
param(
    [string]$ExePath = '',

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)][string]$Pfx,
    [Parameter(ParameterSetName = 'Pfx')][string]$PfxPassword = '',

    [Parameter(ParameterSetName = 'Thumbprint', Mandatory = $true)][string]$Thumbprint,

    [Parameter(ParameterSetName = 'SelfSigned')][switch]$SelfSigned,
    [Parameter(ParameterSetName = 'SelfSigned')][string]$Subject = 'CN=ProcPilot Self-Signed (dev only), O=ProcPilot',
    [Parameter(ParameterSetName = 'SelfSigned')][switch]$TrustLocal,

    [switch]$NoTimestamp,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ExePath) { $ExePath = Join-Path $scriptDir '..\release\ProcPilot.exe' }
$exe = (Resolve-Path -LiteralPath $ExePath).Path
Write-Step "目标程序：$exe"

switch ($PSCmdlet.ParameterSetName) {
    'Pfx' {
        Write-Step "从 PFX 读取证书：$Pfx"
        $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        $cert = Get-PfxCertificate -FilePath $Pfx -Password $secure
        if (-not $cert.HasPrivateKey) {
            $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                (Resolve-Path $Pfx).Path, $PfxPassword, $flags)
        }
    }
    'Thumbprint' {
        Write-Step "从证书存储读取：$Thumbprint"
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
        if (-not $cert) { throw "证书存储里找不到指纹为 $Thumbprint 的证书" }
    }
    default {
        Write-Step '自签名证书（仅本机测试用）'
        $certs = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object {
                $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
                ($_.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3')
            } | Sort-Object NotAfter -Descending
        if ($certs) {
            $cert = $certs[0]
            Write-Host ("    复用：" + $cert.Thumbprint)
        } else {
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
                -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
                -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
                -FriendlyName 'ProcPilot Code Signing (dev only)'
            Write-Host ("    已生成：" + $cert.Thumbprint)
        }

        if ($TrustLocal) {
            Write-Step '把自签名证书加入【当前用户】受信任根证书 / 受信任发布者（仅本机生效）'
            foreach ($storeName in @('Root', 'TrustedPublisher')) {
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
                $store.Open('ReadWrite')
                try {
                    if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
                        $store.Add($cert); Write-Host "    已加入 $storeName"
                    }
                } finally { $store.Close() }
            }
        }
    }
}

$hasCodeSigning = $cert.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3'
if (-not $hasCodeSigning) { Write-Warning '该证书没有「代码签名」用途（EKU 1.3.6.1.5.5.7.3.3），签名可能不被接受。' }

Write-Step '签名（SHA256）'
$sigArgs = @{ FilePath = $exe; Certificate = $cert; HashAlgorithm = 'SHA256' }
if (-not $NoTimestamp -and $PSCmdlet.ParameterSetName -ne 'SelfSigned') {
    Write-Host ("    时间戳：$TimestampServer")
    $sigArgs['TimestampServer'] = $TimestampServer
} else {
    Write-Host '    跳过时间戳'
}
$result = Set-AuthenticodeSignature @sigArgs
if ($result.Status -ne 'Valid') { Write-Warning ("签名状态：{0} — {1}" -f $result.Status, $result.StatusMessage) }

Write-Step 'Unblock-File（去掉 Mark-of-the-Web）'
try { Unblock-File -LiteralPath $exe -ErrorAction Stop; Write-Host '    已解除阻止' }
catch { Write-Host ("    无需解除或失败：{0}" -f $_.Exception.Message) }

Write-Step '校验'
$sig = Get-AuthenticodeSignature -LiteralPath $exe
Write-Host ("    状态：{0}" -f $sig.Status)
Write-Host ("    主体：{0}" -f $sig.SignerCertificate.Subject)
if ($sig.SignTime) { Write-Host ("    签名时间：{0}" -f $sig.SignTime) }
if ($sig.Status -ne 'Valid') { Write-Error "签名未通过校验（$($sig.Status)）"; exit 1 }
Write-Step '完成'
