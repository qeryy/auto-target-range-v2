# 深浅主题检查
#
# ## 为什么需要这个
#
# 2026-11-22 用户连着两轮说「浅色适配不好」「还是有 bug」。第一轮改了
# AccentInk / SessionItem 混色 / FlatButton 写死色 —— 全是真问题，但侧栏
# 依旧 64% 是深色。根因是 WinForms 的 BackColor 是值类型：BuildShell 在
# ApplyTheme 之前跑，控件把当时的深色值拷死了。
#
# 这个脚本验四件事，靠**主色占比**而不是「取样点对不对」：
#
#   1. 深色启动   → 侧栏主色应是深色 Sidebar（#17171A）
#   2. 浅色启动   → 侧栏主色应是浅色 Sidebar（#F0F2F5）
#   3. 深色 + 切一次 → 应变浅
#   4. 浅色 + 切一次 → 应变深
#
# 第 3、4 项验的是 Theme.ApplyTheme(dark, root) 的控件重着色
# （只 Invalidate 重绘不够，重绘画的还是过期的 BackColor）。
#
# ## 用法
#
#   powershell -ExecutionPolicy Bypass -File build\theme_check.ps1
#
# 退出码：0 = 全过，1 = 有失败项。

[CmdletBinding()]
param(
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
$exe = Join-Path $appRoot 'out\灵枢.exe'
$reportDir = Join-Path $appRoot 'out\reports\theme'
$cfgPath = Join-Path $appRoot 'out\data\config.json'
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\theme-report.txt' }
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host "找不到 exe：$exe" -ForegroundColor Red
    Write-Host "先编译：powershell -File build\build.ps1" -ForegroundColor Yellow
    exit 2
}

Add-Type -AssemblyName System.Drawing

# ---- 侧栏两种主题下应该是什么色（与 Theme.cs 的 Sidebar 对应）----
$darkSidebar  = [System.Drawing.Color]::FromArgb(0x17, 0x17, 0x1A)
$lightSidebar = [System.Drawing.Color]::FromArgb(0xF0, 0xF2, 0xF5)
$minShare = 0.85   # 侧栏主色至少要占 85%（还有分隔线/文字/圆角）

$issues = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 深浅主题检查")
$lines.Add("")

function Set-Theme([string]$mode) {
    $json = '{"SchemaVersion":1,"DefaultModel":"","RecallLimit":6,"DistillEnabled":true,"WorkspaceRoot":"","ThemeMode":"' + $mode + '"}'
    [System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Kill-App {
    Get-Process -Name '灵枢' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 900
}

function Shot([string]$name, [bool]$cycle) {
    Kill-App
    $png = Join-Path $reportDir ($name + '.png')
    Remove-Item $png -Force -ErrorAction SilentlyContinue
    $args = @('--page', 'home', '--screenshot', $png)
    if ($cycle) { $args = @('--page', 'home', '--theme-cycle', '--screenshot', $png) }
    $p = Start-Process -FilePath $exe -ArgumentList $args -PassThru
    $ok = $p.WaitForExit(40000)
    if (-not $ok) { try { $p.Kill() } catch { } }
    if (-not (Test-Path $png)) { return $null }
    return $png
}

# 侧栏主色统计：只看前 240px，隔 3 行采样
function SidebarTop($png) {
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    try {
        $w = [Math]::Min(240, $bmp.Width)
        $h = $bmp.Height
        $counts = @{}
        $total = 0
        for ($y = 0; $y -lt $h; $y += 3) {
            for ($x = 0; $x -lt $w; $x += 2) {
                $c = $bmp.GetPixel($x, $y)
                $k = $c.R.ToString('X2') + $c.G.ToString('X2') + $c.B.ToString('X2')
                if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 }
                $total++
            }
        }
        $bestKey = $null
        $bestN = 0
        foreach ($k in $counts.Keys) {
            if ($counts[$k] -gt $bestN) { $bestN = $counts[$k]; $bestKey = $k }
        }
        return @{ Key = $bestKey; Share = [double]$bestN / [double]$total; Total = $total }
    }
    finally { $bmp.Dispose() }
}

function Check([string]$label, $png, $want) {
    if ($null -eq $png) {
        $issues.Add("$label：截图失败")
        $lines.Add("  [FAIL] $label  截图失败")
        return
    }
    $top = SidebarTop $png
    $wantKey = $want.R.ToString('X2') + $want.G.ToString('X2') + $want.B.ToString('X2')
    $ok = ($top.Key -eq $wantKey) -and ($top.Share -ge $minShare)
    $line = "  " + $(if ($ok) { "[ OK ]" } else { "[FAIL]" }) + " $label  侧栏主色 #$($top.Key) 占 $([Math]::Round($top.Share*100,1))%  期望 #$wantKey"
    $lines.Add($line)
    if (-not $ok) { $issues.Add("$label 侧栏主色 #$($top.Key) 占 $([Math]::Round($top.Share*100,1))%，期望 #$wantKey 且 ≥ $([Math]::Round($minShare*100))%") }
}

Write-Host "灵枢桌面版 · 深浅主题检查" -ForegroundColor Cyan

# 1) 深色启动
Set-Theme 'dark'
Check '深色启动' (Shot 'start-dark' $false) $darkSidebar

# 2) 浅色启动
Set-Theme 'light'
Check '浅色启动' (Shot 'start-light' $false) $lightSidebar

# 3) 深色启动 + 切一次 → 应变浅
Set-Theme 'dark'
Check '深色启动后切一次' (Shot 'cycle-from-dark' $true) $lightSidebar

# 4) 浅色启动 + 切一次 → 应变深
Set-Theme 'light'
Check '浅色启动后切一次' (Shot 'cycle-from-light' $true) $darkSidebar

# 恢复默认（深色）
Set-Theme 'dark'
Kill-App

$lines.Add("")
$lines.Add("失败项: " + $issues.Count)
[System.IO.File]::WriteAllLines($OutFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
$lines | ForEach-Object { Write-Host $_ }

if ($issues.Count -gt 0) {
    Write-Host ""
    Write-Host ("深浅主题检查：{0} 项失败（报告 {1}）" -f $issues.Count, $OutFile) -ForegroundColor Red
    foreach ($i in $issues) { Write-Host ("  - " + $i) -ForegroundColor Red }
    exit 1
}
Write-Host ""
Write-Host "深浅主题检查全部通过（报告 $OutFile）" -ForegroundColor Green
exit 0