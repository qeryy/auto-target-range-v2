# 会话渲染性能检查
#
# ## 为什么单独一个脚本
#
# `check_all` 的 8 项是「结构 / 布局 / 行为」，都不管「快不快」。
# 但 182 轮给气泡加了 Markdown 分块渲染之后，每条消息的开销从
# 「一次 MeasureText」变成「N 块 × MeasureText」——这是个**真实的风险**。
#
# 单列一个脚本的理由：
#   · 它要造 5000 条消息的会话，跑十几秒，放进 check_all 会拖慢每一轮
#   · 性能只在改渲染代码时需要看
#
# ## 编译与运行方式
#
# 照 `e2e_check.ps1` 的做法：
#   · 临时目录放**项目目录下**（%TEMP% 下执行 exe 会被拒）
#   · 用 `ProcessStartInfo` + `Process.Start`，不用 `& $exe`
#     ——后者在这个环境里常被判 Access denied（杀软扫描窗口）
#   · 输出用 UTF-8 编码读，中文不乱码

[CmdletBinding()]
param(
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\perf-report.txt' }
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Host "  [FAIL] csc.exe not found"; exit 1 }

$tmpName = "_perf_" + [guid]::NewGuid().ToString('N').Substring(0, 8)
$tmp = Join-Path $appRoot $tmpName
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    $skills = Join-Path $appRoot 'src\Resources\skills.json'
    $sources = @((Join-Path $buildDir 'tests\SessionPerf.cs'))
    $sources += Get-ChildItem (Join-Path $appRoot 'src') -Recurse -Filter '*.cs' |
        Where-Object { $_.Name -ne 'Program.cs' } |
        ForEach-Object { $_.FullName }

    $exe = Join-Path $tmp 'perf.exe'
    $cscArgs = @(
        '/nologo', '/target:exe', '/main:SessionPerf', "/out:$exe",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        '/r:System.Security.dll', '/r:System.Net.Http.dll',
        "/resource:$skills,skills.json"
    ) + $sources

    $compile = & $csc @cscArgs 2>&1
    if (-not (Test-Path $exe)) {
        Write-Host "  [FAIL] build error:"
        $compile | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
        exit 1
    }

    $dataDir = Join-Path $tmp 'data'
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
    if (-not $p.WaitForExit(300000)) { try { $p.Kill() } catch { } }
    $code = $p.ExitCode

    $header = @()
    $header += "灵枢桌面版 · 会话渲染性能"
    $header += "时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    $header += ""
    $header | ForEach-Object { Write-Host $_ }

    ($out -split "`n") | ForEach-Object { Write-Host $_ }

    $passCount = ([regex]::Matches($out, '\[PASS\]')).Count
    $failCount = ([regex]::Matches($out, '\[FAIL\]')).Count

    $all = $header + ($out -split "`n")
    $all -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8

    Write-Host ""
    if ($code -ne 0 -or $failCount -gt 0) {
        Write-Host "  [FAIL] 性能断言 [$passCount] 通过 / [$failCount] 不通过（exit $code）" -ForegroundColor Red
        exit 1
    }
    Write-Host "  全通过 $passCount 项性能断言" -ForegroundColor Green
    Write-Host "  报告: $OutFile" -ForegroundColor Cyan
}
finally {
    Start-Sleep -Milliseconds 200
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
