#requires -Version 5.1
<#
.SYNOPSIS
    解除 ProcPilot.exe（及其目录下文件）的「网络来源标记」（Mark-of-the-Web），
    让 SmartScreen 不再弹「Windows 已保护你的电脑 / 无法识别的应用」。

.DESCRIPTION
    Windows 会给"从浏览器、IM、邮件、局域网共享、资源管理器解压 zip"得到的文件打上
    Zone.Identifier 备用流（MOTW）。带这个标记的未获得 SmartScreen 信誉的程序首次运行
    会被拦一次。本脚本对它执行 Unblock-File（等价于右键属性 → 解除锁定），
    这是【本机一次性】操作，不需要证书、不需要管理员。

    分发提示：
      - 给别人时把 zip 传过去，并让对方解压后运行本脚本（或手动「解除锁定」）；
      - 局域网共享 / IM 传 exe 会重新打标记，传 zip 更容易说明；
      - 想要"别人下载后也不提示"，只有正式代码签名证书 + 信誉积累这一条路（见 README）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\unblock-exe.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\unblock-exe.ps1 -Path .\release
#>
[CmdletBinding()]
param(
    [string]$Path = ''
)

$ErrorActionPreference = 'Continue'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Path) { $Path = Join-Path $scriptDir '..\release' }
$target = (Resolve-Path -LiteralPath $Path).Path

Write-Host "==> 目标：$target"

$files = @()
if (Test-Path -LiteralPath $target -PathType Leaf) {
    $files = @(Get-Item -LiteralPath $target)
} else {
    $files = @(Get-ChildItem -LiteralPath $target -File -Recurse -Include '*.exe', '*.dll', '*.cer', '*.ps1', '*.cmd')
}

$changed = 0
foreach ($f in $files) {
    $zone = Get-Item -LiteralPath $f.FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue
    if ($zone) {
        try {
            Unblock-File -LiteralPath $f.FullName -ErrorAction Stop
            Write-Host ("    已解除标记：" + $f.Name)
            $changed++
        } catch {
            Write-Warning ("    解除失败 {0}：{1}" -f $f.Name, $_.Exception.Message)
        }
    }
}

Write-Host ("==> 完成，共解除 {0} 个文件的网络来源标记（本来就没有标记的文件不受影响）。" -f $changed)
Write-Host '    如果仍然弹 SmartScreen：确认运行的是刚解除标记的那份文件，且没有再次通过网络复制。'
