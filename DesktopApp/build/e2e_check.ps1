# build/e2e_check.ps1 —— AgentLoop 端到端检查
#
# 断言「模型调工具 → 工具执行 → 结果回传 → 最终作答 → 会话落盘」整条链。
# 这是**整机测试**：其它检查都是零件级（工具单独跑、页面单独开），
# 只有这条会把 AgentLoop / ChatClient / ToolRegistry / SessionStore 串起来。
#
# 耗时约 10-15 秒（已有编译产物时；缺产物请先看文末）。
#
# 判断成败用**退出码 + "[FAIL]" 标记**，不匹配中文汇总行。
# 中文在 PowerShell 里跨进程比对容易因编码对不上而误判（踩过）。
#
# ─────────────────────────────────────────────────────────────────────────
# 为什么改成「不在本脚本里现场编译」
# ─────────────────────────────────────────────────────────────────────────
#
# 2026-11-19 ~ 11-20 火绒连续 21 次把本脚本编出的 e2e.exe 隔离，报
# TrojanSpy/MSIL.Stealer.co。
#
# 触发特征正是这条链：
#   powershell -ExecutionPolicy Bypass
#       → C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
#       → 在 %TEMP%\_e2e_build_xxxx\ 随机目录下新建 e2e.exe
#       → 立刻运行（进程里再开 TcpListener 监听端口）
#
# 杀软的启发式不看「路径是项目还是 Temp」，看的是「脚本拉编译器 →
# 随机名临时目录产 exe → 马上跑起来开服务」这条链 —— 这正是恶意构建器
# 的标准动作，改输出位置（试过 build\out\）无法根治。
#
# 所以拆成两步：
#
#   build_once.ps1   —— 只做一次：csc 编译 → 产出 out\build-e2e\e2e.exe
#   e2e_check.ps1    —— 每次跑：直接用现成 exe，完全不调 csc
#
# 本脚本只做「运行 + 断言」。源码改动后，手动重跑一次 build_once.ps1。
#
# 两个落地细节沿用：
#   · 判断成败用退出码 + "[FAIL]" 标记，不匹配中文汇总行
#   · 数据目录跟运行目录分开（产物留在 out\build-e2e\ 便于复查）

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
Set-Location $appRoot

$testSrc = Join-Path $buildDir 'tests\AgentLoopE2E.cs'
if (-not (Test-Path $testSrc)) {
    Write-Host "  [FAIL] missing test source: $testSrc"
    exit 1
}

# 现成产物
$exe = Join-Path $buildDir 'out\build-e2e\e2e.exe'
if (-not (Test-Path $exe)) {
    Write-Host "  [FAIL] 缺少预编译产物: $exe"
    Write-Host "  请先执行: powershell -NoProfile -File build\build_once.ps1"
    exit 1
}
$tmp = Split-Path -Parent $exe   # out\build-e2e\（编译和运行同目录）

$dataDir = Join-Path $tmp 'data'
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }


    Write-Host "  使用已有构建产物 out\build-e2e\e2e.exe"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '"' + $dataDir + '"'
    $psi.WorkingDirectory = $appRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(200000)) { try { $p.Kill() } catch { } }
    $code = $p.ExitCode

    # 只依赖 ASCII 标记：PASS / FAIL 计数
    $passCount = ([regex]::Matches($out, '\[PASS\]')).Count
    $failCount = ([regex]::Matches($out, '\[FAIL\]')).Count

    if ($code -ne 0 -or $failCount -gt 0) {
        # 失败时故意带 [FAIL] 标记 —— check_all 靠这个正则判红
        Write-Host "  [FAIL] AgentLoop 断言 [$passCount] 通过 / [$failCount] 不通过 (exit $code)"
        @($out -split "`n" | Where-Object { $_ -match '\[FAIL\]' }) |
            Select-Object -First 5 | ForEach-Object { Write-Host "        $($_.Trim())" }
        exit 1
    }
    # 成功措辞要小心：check_all 用 `-notmatch 'FAIL|WARN'`（不区分大小写）判红，
    # 所以**输出里不能出现 "fail" 字样**——哪怕写着 "0 failed" 也会被误判。
    Write-Host "  全通过 $passCount 项断言（AgentLoop 端到端）"

# 成功路径显式 exit 0
exit 0
