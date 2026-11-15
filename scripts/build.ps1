#requires -Version 5.1
<#
.SYNOPSIS
    一键构建：单文件自包含发布。

.DESCRIPTION
    需要 .NET SDK 8（dotnet 在 PATH 里，或用 -Dotnet 指定）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1

#>
[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDir = ''
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

# 顺手去掉网络来源标记（MOTW），避免下载后首次运行被拦
try { Unblock-File -LiteralPath $exe -ErrorAction SilentlyContinue } catch { }

$info = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()

Write-Host ''
Write-Host '==> 产物' -ForegroundColor Cyan
Write-Host ("    文件：{0}" -f $exe)
Write-Host ("    大小：{0:N0} 字节（{1:N1} MB）" -f $info.Length, ($info.Length / 1MB))
Write-Host ("    SHA256：{0}" -f $hash)
Write-Host ''
