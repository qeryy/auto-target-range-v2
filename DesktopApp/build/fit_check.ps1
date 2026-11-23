# 排版贴合检查（对话框 + 13 页 + 会话气泡）
#
# clip_check 走 UIA，只看得到有 Name 的控件、且只量宽度。字号或 DPI 一变，
# 真正先坏的是「文字和装它的盒子」的关系：说明标签纵向被裁、标签顶进输入框、
# 底部按钮掉出客户区。这些 UIA 看不见，所以用控件树离屏量。
#
# 用 build_once.ps1 编出的现成产物（build\out\build-fit\fit.exe），
# 跑一遍，把明细写到 out\reports\fit-report.txt。
#
# ─────────────────────────────────────────────────────────────────────────
# 为什么不再现场编译（2026-11-22 与 e2e_check 同步）
# ─────────────────────────────────────────────────────────────────────────
#
# 「powershell 拉 csc → 临时目录产 exe → 立刻跑」这条链被火绒启发式
# 误判为 TrojanSpy/MSIL.Stealer.co。现在本脚本只负责运行现成的 fit.exe，
# 源码变更由 build_once.ps1 统一负责编译。

[CmdletBinding()]
param(
    [string]$OutFile = '',
    [string]$Shots = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\fit-report.txt' }
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

Set-Location $appRoot

$testSrc = Join-Path $buildDir 'tests\LayoutFit.cs'
if (-not (Test-Path $testSrc)) { Write-Host "  [FAIL] missing source: $testSrc"; exit 1 }

# 现成产物
$exe = Join-Path $buildDir 'out\build-fit\fit.exe'
if (-not (Test-Path $exe)) {
    Write-Host "  [FAIL] 缺少预编译产物: $exe"
    Write-Host "  请先执行: powershell -NoProfile -File build\build_once.ps1"
    exit 1
}
$tmp = Split-Path -Parent $exe

Write-Host "  使用已有构建产物 out\build-fit\fit.exe"

$dataDir = Join-Path $tmp 'data'
$argLine = '"' + $dataDir + '"'
if ($Shots) {
    if (-not [System.IO.Path]::IsPathRooted($Shots)) { $Shots = Join-Path $appRoot $Shots }
    New-Item -ItemType Directory -Path $Shots -Force | Out-Null
    $argLine = $argLine + ' "' + $Shots + '"'
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = $argLine
$psi.WorkingDirectory = $appRoot
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

$p = [System.Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEnd()
$err = $p.StandardError.ReadToEnd()
if (-not $p.WaitForExit(180000)) { try { $p.Kill() } catch { } }
$code = $p.ExitCode

$passCount = ([regex]::Matches($out, '\[PASS\]')).Count
$failCount = ([regex]::Matches($out, '\[FAIL\]')).Count

# 写报告
$report = @()
$report += $out -split "`r?`n"
if ($err.Trim()) { $report += ''; $report += '── stderr ──'; $report += $err -split "`r?`n" }
[IO.File]::WriteAllLines($OutFile, $report, (New-Object Text.UTF8Encoding($false)))

if ($code -ne 0 -or $failCount -gt 0) {
    Write-Host "  [FAIL] LayoutFit 断言 [$passCount] 通过 / [$failCount] 不通过 (exit $code)"
    @($out -split "`n" | Where-Object { $_ -match '\[FAIL\]' }) |
        Select-Object -First 5 | ForEach-Object { Write-Host "        $($_.Trim())" }
    exit 1
}
Write-Host "  全通过 $passCount 项断言（排版贴合）"
exit 0
