# 构建脚本：把 CleanText.cs 编译成单文件 exe（无需 Python / 无需联网）
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$root = Split-Path -Parent $here
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }

# 保证源码是带 BOM 的 UTF-8，否则 csc 会按 ANSI 解析中文字符串
$src = Join-Path $here "CleanText.cs"
$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($src, $text, (New-Object System.Text.UTF8Encoding($true)))

$target = Join-Path $root "文字清洗工具.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    /win32manifest:"$here\app.manifest" /win32icon:"$here\app.ico" /out:"$target" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll /reference:System.Security.dll `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "编译失败" }
Write-Output ("生成: {0} ({1} 字节)" -f $target, (Get-Item $target).Length)

# 自检（12 项清洗逻辑用例）
$dbg = Join-Path $here "selftest.exe"
& $csc /nologo /target:exe /platform:anycpu /out:"$dbg" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll /reference:System.Security.dll `
    "$src"
Remove-Item (Join-Path $here "selftest.txt") -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $dbg -ArgumentList "--selftest" -Wait -PassThru
Get-Content (Join-Path $here "selftest.txt") -Encoding UTF8
Remove-Item $dbg -ErrorAction SilentlyContinue
