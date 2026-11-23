# 全量检查
#
# 一个命令跑完所有自检，最后给一张汇总表。
#
# 十一个检查各自独立（web_parity / tool_parity / doc / e2e / smoke / layout / clip / overflow / fit / theme / session），
# 平时改一处跑一个就够；交付前跑这个。
#
# 用法：
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\check_all.ps1')))"
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\check_all.ps1')))"   # 原参数：-Quick   # 跳过三个界面检查（省 3 分钟）

[CmdletBinding()]
param(
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
Set-Location $appRoot

$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Head([string]$text) {
    Write-Host ""
    Write-Host ("=" * 62) -ForegroundColor DarkGray
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ("=" * 62) -ForegroundColor DarkGray
}

function Run-Check([string]$name, [string]$script, [string]$args, [string]$expect) {
    $result = [PSCustomObject]@{
        Name = $name
        Ok   = $false
        Info = ""
        Ms   = 0
    }

    Write-Host ""
    Write-Host "▶ $name" -ForegroundColor White

    if (-not (Test-Path $script)) {
        $result.Info = "脚本不存在"
        Write-Host "  跳过：$script 不存在" -ForegroundColor Yellow
        return $result
    }

    $t = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        # **每个检查都要有超时**。
        # 153 轮踩过：某个检查卡住（窗口/UIA 卡死）会让整个 check_all 挂死，
        # 十分钟不返回、也没有任何输出，只能手动杀。检查脚本自己不能变成
        # 「需要人盯着」的东西。
        #
        # 用 Process + WaitForExit(ms) 而不是 `& powershell` —— 后者没有超时。
        # ReadToEnd 必须异步起，否则子进程输出多的时候会互相等死。
        $timeoutMs = 300000
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell'
        # 本机脚本策略拒绝无签名 -File；交互模式（-Command）是策略放行的运行方式。
        # 顺带 UTF8 读脚本，避免 PS 5.1 按 ANSI 读中文路径乱码。
        $psi.Arguments = "-NoProfile -Command `"`$sb = [scriptblock]::Create((Get-Content -Raw -Encoding UTF8 '$script')); & `$sb $args`""
        $psi.WorkingDirectory = $appRoot
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

        $proc = [System.Diagnostics.Process]::Start($psi)
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($timeoutMs)) {
            try { $proc.Kill() } catch { }
            $t.Stop()
            $result.Ms = $t.ElapsedMilliseconds
            $result.Ok = $false
            $result.Info = "超时（超过 $([int]($timeoutMs/1000)) 秒），已强制终止 —— 这个检查可能卡住了"
            Write-Host "  $($result.Info)" -ForegroundColor Red
            return $result
        }
        $stdout = $outTask.Result
        $stderr = $errTask.Result
        $proc.Dispose()
        $t.Stop()
        $result.Ms = $t.ElapsedMilliseconds
        $out = @()
        # **过滤空行**：-split 会在末尾留一个空串，取 Last 1 时就拿到空的了
        # （153 轮改超时逻辑时踩到：web_parity 的 [info] 说明突然变空）。
        if ($stdout) { $out += @($stdout -split "`r?`n" | Where-Object { $_.Trim() -ne '' }) }
        # **stderr 不混进 stdout**。混进去会把 PowerShell 自己的报错
        # （Split-Path 收到空串、Test-Path 非法字符之类）当成「检查结论」
        # 显示出来 —— 而且因为报错里没有 FAIL 字样，还照样判「通过」。
        # 2026-11-22 就是这么发现 web_parity / tool_parity 假通过了好几轮。
        $errLines = @()
        if ($stderr) { $errLines += @($stderr -split "`r?`n" | Where-Object { $_.Trim() -ne '' }) }

        # 从输出里挑有意义的行
        $lines = @($out | ForEach-Object { "$_" } | Where-Object { $_ -notmatch 'libpng warning' })
        $summary = $lines | Where-Object {
            $_ -match '通过\s*\d+\s*/\s*失败|处可能被裁|溢出页面数|文档数字与代码一致|0 处可能被裁|全通过|WARN|FAIL'
        } | Select-Object -Last 3

        $result.Info = if ($summary) { ($summary -join ' | ') }
                       elseif ($lines.Count -gt 0) { ($lines | Select-Object -Last 1) }
                       elseif ($errLines.Count -gt 0) { '脚本报错：' + ($errLines | Select-Object -Last 1) }
                       else { '（无输出）' }
        # [WARN] 也算没通过——之前漏了这个，about 页溢出 41px 却显示「全部通过」。
        # 但「0 处可能被裁」「溢出页面数: 0」是**通过**的表述，不能误判成失败。
        # 「脚本报错：」「（无输出）」同样判失败 —— 脚本自己崩了不能算这项过了。
        $result.Ok = ($result.Info -notmatch 'FAIL|WARN|⚠') -and
                     ($result.Info -notmatch '[1-9]\d* 处可能被裁') -and
                     ($result.Info -notmatch '溢出页面数: [1-9]') -and
                     ($result.Info -notmatch '有 \d+ 处') -and
                     ($result.Info -notmatch '脚本报错：') -and
                     ($result.Info -notmatch '（无输出）')

        Write-Host "  $($result.Info)" -ForegroundColor $(if ($result.Ok) { 'Green' } else { 'Yellow' })
    }
    catch {
        $t.Stop()
        $result.Ms = $t.ElapsedMilliseconds
        $result.Info = "异常：" + $_.Exception.Message
        Write-Host "  $($result.Info)" -ForegroundColor Red
    }
    return $result
}

# 先确认产物在
$exe = Join-Path $appRoot 'out\灵枢.exe'
if (-not (Test-Path $exe)) {
    Write-Host "产物不存在：$exe" -ForegroundColor Red
    Write-Host "先跑 build.ps1" -ForegroundColor Yellow
    exit 1
}

$sizeKB = [math]::Round((Get-Item $exe).Length / 1KB)
$srcFiles = Get-ChildItem 'src' -Recurse -Filter '*.cs'
$srcLines = (Get-Content ($srcFiles | ForEach-Object { $_.FullName }) -Encoding UTF8 | Measure-Object -Line).Lines

Write-Head "灵枢桌面版 · 全量检查"
Write-Host "  产物：$sizeKB KB  ·  源码：$($srcFiles.Count) 个文件 / $srcLines 行"

$results = New-Object System.Collections.Generic.List[object]

# 关掉可能在跑的实例，免得检查时抢窗口
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Write-Head "1 / 11  功能面对照（web 区块 ⊆ 桌面页面）"
$results.Add((Run-Check "web 功能面全覆盖" 'build\web_parity.ps1' '' ''))

Write-Head "2 / 11  工具与 agent 白名单一致"
$results.Add((Run-Check "工具白名单一致" 'build\tool_parity.ps1' '' ''))

Write-Head "3 / 11  文档一致性"
$results.Add((Run-Check "文档数字与代码一致" 'build\doc_check.ps1' '' ''))

if (-not $Quick) {
    Write-Head "4 / 11  整机链路（AgentLoop 端到端）"
    $results.Add((Run-Check "AgentLoop 端到端" 'build\e2e_check.ps1' '' ''))

    Write-Head "5 / 11  逐页功能"
    $results.Add((Run-Check "13 页冒烟" 'build\smoke.ps1' '' ''))

    Write-Head "6 / 11  布局"
    $results.Add((Run-Check "控件越界与零尺寸" 'build\layout_check.ps1' '' ''))

    Write-Head "7 / 11  文字裁切"
    $results.Add((Run-Check "文字是否被裁" 'build\clip_check.ps1' '' ''))

    Write-Head "8 / 11  内容溢出"
    $results.Add((Run-Check "内容是否超出窗口" 'build\overflow_check.ps1' '' ''))

    Write-Head "9 / 11  排版贴合（对话框 + 页面 + 气泡，控件树自量）"
    $results.Add((Run-Check "文字与盒子的贴合" 'build\fit_check.ps1' '' ''))
} else {
    Write-Host ""
    Write-Host "（-Quick：跳过 4-9 六项耗时检查，省约 3 分钟）" -ForegroundColor DarkGray
}

# 深浅主题：11 秒，Quick 模式也跑 —— 这是「界面对不对」的第一道关，
# 用户报过两次「浅色适配不好」，不能靠人眼看。
Write-Head "10 / 11  深浅主题（启动 + 运行时切换）"
$results.Add((Run-Check "深浅主题启动与切换" 'build\theme_check.ps1' '' ''))

# 会话端到端：真机开 exe、打开会话、自动发一条、只判结果（会话文件里的消息 + 阶段）。
# e2e_check 盖的是 AgentLoop 逻辑层，这一项补的才是「界面 → SendAsync」那一段。
Write-Head "11 / 11  会话端到端（真机 UI 链路）"
$results.Add((Run-Check "会话端到端" 'build\session_ui_check.ps1' '' '' ))

$sw.Stop()

# 汇总表
Write-Head "汇总"
Write-Host ""
Write-Host ("  {0,-24} {1,-8} {2,-10} {3}" -f '检查项', '结果', '耗时', '说明') -ForegroundColor DarkGray
Write-Host ("  " + ("-" * 76)) -ForegroundColor DarkGray

$failed = 0
foreach ($r in $results) {
    $flag = if ($r.Ok) { '通过' } else { '需看' }
    $color = if ($r.Ok) { 'Green' } else { 'Yellow' }
    if (-not $r.Ok) { $failed++ }
    $info = $r.Info
    if ($info.Length -gt 40) { $info = $info.Substring(0, 40) + '…' }
    Write-Host ("  {0,-24} {1,-8} {2,-10} {3}" -f $r.Name, $flag, "$($r.Ms)ms", $info) -ForegroundColor $color
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "  全部通过  ·  总耗时 $([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒" -ForegroundColor Green
    exit 0
} else {
    Write-Host "  $failed 项需要人工看一下  ·  总耗时 $([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒" -ForegroundColor Yellow
    exit 1
}
