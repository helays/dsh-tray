# ============================================================================
# build-exe.ps1
# 用 .NET C# 编译器(经 Add-Type / csc)把 src/DshTray.cs 编译为独立 bin/DshTray.exe。
# DshTray.exe 是一个 WinForms 托盘宿主, 不显示窗口。
#
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File build-exe.ps1
# 输出: bin/DshTray.exe
# ============================================================================
[CmdletBinding()] param()

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$src   = Join-Path $root 'src\DshTray.cs'
$out   = Join-Path $root 'bin\DshTray.exe'
New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null

if (-not (Test-Path -LiteralPath $src)) { throw "missing: $src" }

# 定位 .NET Framework 编译引用程序集
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework'
$csc = @(
    (Join-Path $fw 'v4.0.30319\csc.exe'),
    (Join-Path $fw 'v3.5\csc.exe'),
    (Join-Path $fw '\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $csc) { throw '未找到 csc.exe' }

# 引用的程序集 (从 csc 所在 Framework 目录解析)
$asmDir = Split-Path -Parent $csc
$refs = @()
foreach ($a in @('System.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Core.dll')) {
    $p = Join-Path $asmDir $a
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少引用程序集: $p" }
    $refs += "/r:$p"
}

# 鲸鱼图标: 作为 Win32 资源(文件图标) + 嵌入资源(运行时可读)打进 exe
$iconFile = Join-Path $root 'assets\DeepSeekWhale.ico'
$iconEmb = @()
if (Test-Path -LiteralPath $iconFile) {
    $iconEmb += '/win32icon:' + $iconFile          # exe 文件图标
    $iconEmb += '/resource:' + $iconFile + ',DeepSeekWhale.ico'   # 运行时可读资源
} else {
    Write-Host "[build-exe] 警告: 未找到 $iconFile, exe 将不含内嵌图标" -ForegroundColor Yellow
}

$cscArgs = @(
    '/nologo',
    '/target:winexe',          # 无控制台窗口
    '/optimize',
    ('/out:' + $out)           # 必须加括号, 数组字面量里拼接会被吞掉
) + $iconEmb + $refs + @($src)

Write-Host "[build-exe] 编译..." 
$errLogPath = Join-Path $root 'bin\csc.stderr.log'
& $csc $cscArgs 2> $errLogPath
$exit = $LASTEXITCODE
$errLog = Get-Content -LiteralPath $errLogPath -Raw -ErrorAction SilentlyContinue

if ($exit -eq 0) {
    Write-Host "[build-exe] OK: $out"
    if (Test-Path -LiteralPath $errLogPath) { Remove-Item -LiteralPath $errLogPath -Force }
    Get-Item -LiteralPath $out | Select-Object Name, Length, LastWriteTime
} else {
    Write-Host "[build-exe] 编译失败 (exit=$exit)" -ForegroundColor Red
    if ($errLog) { Write-Host $errLog -ForegroundColor Red }
    exit 1
}
