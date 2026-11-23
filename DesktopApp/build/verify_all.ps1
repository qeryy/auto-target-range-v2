# 一次性验证：把三层检查串起来跑
#
# ## 为什么需要它
#
# 现在的检查分在三个脚本里，各有各的用途和耗时：
#
#   check_all.ps1    9 项结构/布局/行为      ~167 秒
#   perf_check.ps1   6 项渲染性能             ~40 秒
#   color_check.ps1  9 项配色一致性            ~2 秒
#
# 平时改一处只需要跑其中一个。但**攒了一批改动要一次性确认**时
# （比如连续几轮只写不验之后），分开跑三次、还要自己记哪个绿哪个红，
# 容易漏。
#
# 这个脚本就是「跑全部、汇总成一张表」。
#
# ## 用法
#
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\verify_all.ps1')))"
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\verify_all.ps1')))"   # 原参数：-SkipPerf
#
# ## 输出
#
#   逐段打印每个脚本的输出，最后给一张汇总表 + 总退出码。
#   任何一项失败 → 退出码非 0。
#
# ## 注意
#
# `color_check.ps1` 依赖 `docs\screenshots\session.png` 存在且**同时包含
# 用户气泡和助手气泡**（内容要装得进一屏，否则滚动会把用户消息移出可视区）。
# 找不到截图时它会自己提示，不会静默通过。

[CmdletBinding()]
param(
    [switch]$SkipPerf,
    [switch]$SkipColor,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\verify-all.txt' }
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps)) { $ps = 'powershell' }

$results = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[string]
$log.Add("灵枢桌面版 · 一次性验证")
$log.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$log.Add("")

function Run-One([string]$name, [string]$script, [string]$note) {
    Write-Host ""
    Write-Host ("=" * 66) -ForegroundColor DarkGray
    Write-Host ("  $name  ·  $note") -ForegroundColor Cyan
    Write-Host ("=" * 66) -ForegroundColor DarkGray

    $path = Join-Path $buildDir $script
    if (-not (Test-Path $path)) {
        Write-Host "  [SKIP] 找不到 $script" -ForegroundColor Yellow
        $results.Add([PSCustomObject]@{ Name = $name; Code = -1; Note = "脚本不存在" })
        return
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # 用 Process 而不是 `& powershell`：管道中断时 & 会把退出码变成 -1
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ps
    # 本机脚本策略拒绝无签名 -File；交互模式（-Command）是策略放行的运行方式。
    # 顺带 UTF8 读脚本，避免 PS 5.1 按 ANSI 读中文路径乱码。
    $psi.Arguments = '-NoProfile -Command "$sb = [scriptblock]::Create((Get-Content -Raw -Encoding UTF8 ''' + $path + ''')); & $sb"'
    $psi.WorkingDirectory = $appRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($psi)

    # **必须异步起读取，否则会死锁。**
    #
    # 这条经验 check_all.ps1 早就踩过并写了注释（它 L56 原话：
    # 「ReadToEnd 必须异步起，否则子进程输出多的时候会互相等死」），
    # 但这个脚本当时没跟着改。
    #
    # 死锁怎么发生的：先 `StandardOutput.ReadToEnd()` 会一直阻塞到 stdout 关闭；
    # 与此同时子进程如果往 **stderr** 写满了管道缓冲（默认 4KB），它就阻塞在那里，
    # stdout 也就永远不关 —— **两边互相等**。
    #
    # 更糟的是：原来 `WaitForExit(600000)` 写在这两句**后面**，
    # 所以那个「10 分钟超时」在死锁时根本执行不到。
    #
    # 正确做法：两个流都用 ReadToEndAsync 立刻挂起读取，再等进程退出。
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $proc.WaitForExit(600000)) {
        $timedOut = $true
        try { $proc.Kill() } catch { }
    }
    $out = if ($outTask.IsCompleted) { $outTask.Result } else { '' }
    $err = if ($errTask.IsCompleted) { $errTask.Result } else { '' }
    $code = if ($timedOut) { -2 } else { $proc.ExitCode }
    $sw.Stop()
    if ($timedOut) {
        Write-Host "  [TIMEOUT] $script 超过 600 秒，已强制终止" -ForegroundColor Red
        $log.Add("[TIMEOUT] $script 超过 600 秒，已强制终止")
    }

    $out -split "`n" | ForEach-Object { if ($_.Trim()) { Write-Host $_ } }
    if ($err -and $err.Trim()) {
        Write-Host "  --- stderr ---" -ForegroundColor DarkYellow
        $err -split "`n" | ForEach-Object { if ($_.Trim()) { Write-Host $_ } }
    }

    $log.Add("")
    $log.Add("===== $name（$script）=====")
    ($out -split "`n") | ForEach-Object { $log.Add($_) }
    if ($err -and $err.Trim()) {
        $log.Add("--- stderr ---")
        ($err -split "`n") | ForEach-Object { $log.Add($_) }
    }

    $verdict = if ($code -eq 0) { '通过' } else { '失败' }
    $color = if ($code -eq 0) { 'Green' } else { 'Red' }
    Write-Host ""
    Write-Host ("  → $name : $verdict（exit $code，$([math]::Round($sw.Elapsed.TotalSeconds,1))s）") -ForegroundColor $color

    $results.Add([PSCustomObject]@{
        Name = $name
        Code = $code
        Note = ($note + " · " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + "s")
    })
}

# ---- 1) 结构 / 布局 / 行为（含 129 项 e2e 断言）----
Run-One '结构布局行为' 'check_all.ps1' '9 项 + e2e 断言'

# ---- 2) 渲染性能 ----
if (-not $SkipPerf) {
    Run-One '渲染性能' 'perf_check.ps1' '6 项'
}

# ---- 3) 配色一致性 ----
if (-not $SkipColor) {
    Run-One '配色一致性' 'color_check.ps1' '9 项（依赖 docs\screenshots\session.png）'
}

# ---- 汇总 ----
Write-Host ""
Write-Host ("=" * 66) -ForegroundColor DarkGray
Write-Host "  汇总" -ForegroundColor Cyan
Write-Host ("=" * 66) -ForegroundColor DarkGray

$log.Add("")
$log.Add(("=" * 66))
$log.Add("汇总")
$bad = 0
foreach ($r in $results) {
    $mark = if ($r.Code -eq 0) { '[ OK ]' } elseif ($r.Code -eq -1) { '[SKIP]' } else { '[FAIL]' }
    if ($r.Code -ne 0 -and $r.Code -ne -1) { $bad++ }
    $color = if ($r.Code -eq 0) { 'Green' } elseif ($r.Code -eq -1) { 'Yellow' } else { 'Red' }
    $line = ("  {0} {1,-14} {2}" -f $mark, $r.Name, $r.Note)
    Write-Host $line -ForegroundColor $color
    $log.Add($line)
}

Write-Host ""
if ($bad -eq 0) {
    $msg = "全部通过"
    Write-Host "  $msg" -ForegroundColor Green
} else {
    $msg = "$bad 项失败"
    Write-Host "  $msg" -ForegroundColor Red
}
$log.Add("")
$log.Add("  $msg")
$log -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host "  报告: $OutFile" -ForegroundColor Cyan

if ($bad -ne 0) { exit 1 }
exit 0
