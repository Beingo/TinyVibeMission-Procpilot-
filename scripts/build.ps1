#requires -Version 5.1
<#
.SYNOPSIS
    一键构建：单文件自包含发布（可选：用正式证书签名）。

.DESCRIPTION
    需要 .NET SDK 8（dotnet 在 PATH 里，或用 -Dotnet 指定）。

    默认只发布、不签名。要签名时加 -Sign，并给出证书来源：
      -Pfx <file.pfx> -PfxPassword <pwd>
      -Thumbprint <thumb>
    或 -Sign -SelfSigned 用自签名证书（仅本机测试）。

    为什么默认不签名：自签名证书对别人的电脑没有意义（也拿不到 SmartScreen 信誉），
    正式证书请自行准备；见 README「分发、签名与 SmartScreen」。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Sign -Pfx .\mycert.pfx -PfxPassword '***'
#>
[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDir = '',
    [switch]$Sign,
    [string]$Pfx = '',
    [string]$PfxPassword = '',
    [string]$Thumbprint = '',
    [switch]$SelfSigned,
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path -LiteralPath (Join-Path $scriptDir '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $root 'release' }
$out = [IO.Path]::GetFullPath($OutputDir)

Write-Host "==> 发布到 $out" -ForegroundColor Cyan
Push-Location $root
try {
    # -nodeReuse:false / UseSharedCompilation=false：
    #   不留下常驻的 MSBuild / Roslyn 子进程。它们会继承 stdout 管道，
    #   在 CI、远程执行器或"被别的程序捕获输出"的场景下会让父进程等不到 EOF 而假死。
    & $Dotnet publish -c Release -o $out -r win-x64 --self-contained true `
        -nodeReuse:false `
        -p:UseSharedCompilation=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }
} finally { Pop-Location }

$exe = Join-Path $out 'ProcPilot.exe'
if (-not (Test-Path $exe)) { throw "没有生成 $exe" }

if ($Sign) {
    $signArgs = @{ ExePath = $exe }
    if ($Pfx) { $signArgs['Pfx'] = $Pfx; $signArgs['PfxPassword'] = $PfxPassword }
    elseif ($Thumbprint) { $signArgs['Thumbprint'] = $Thumbprint }
    elseif ($SelfSigned) { $signArgs['SelfSigned'] = $true }
    else { throw '指定了 -Sign，但没有给证书：请用 -Pfx / -Thumbprint / -SelfSigned' }
    if ($NoTimestamp) { $signArgs['NoTimestamp'] = $true }
    & (Join-Path $scriptDir 'sign.ps1') @signArgs
    if ($LASTEXITCODE -ne 0) { throw '签名失败' }
} else {
    # 没签名也顺手去掉网络来源标记，避免 SmartScreen 因为 MOTW 而拦
    try { Unblock-File -LiteralPath $exe -ErrorAction SilentlyContinue } catch { }
}

$info = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$sig = Get-AuthenticodeSignature -LiteralPath $exe

Write-Host ''
Write-Host '==> 产物' -ForegroundColor Cyan
Write-Host ("    文件：{0}" -f $exe)
Write-Host ("    大小：{0:N0} 字节（{1:N1} MB）" -f $info.Length, ($info.Length / 1MB))
Write-Host ("    SHA256：{0}" -f $hash)
Write-Host ("    签名：{0}" -f $sig.Status)
Write-Host ''
Write-Host '提示：把 exe 发给别人前，请先看 README「分发、签名与 SmartScreen」一节。'
