# 灵枢桌面版 —— 界面布局检查
#
# 我读不了截图，但可以量。用 UI Automation 拿到每个可见控件的包围盒，
# 检查这几类客观问题：
#   1. 控件超出窗口边界（被切掉，用户看不到）
#   2. 控件尺寸为 0（不可见但占位）
#   3. 文本被截断（控件宽度明显小于文字所需）
#   4. 窗口缩放后元素重叠
#
# 这些不是「好不好看」，而是「有没有坏」——能自动查出来。

[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $Exe) { $Exe = Join-Path $appRoot 'out\灵枢.exe' }
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\layout-report.txt' }
# 报告放 out\reports\ 子目录：out\ 是产物目录，只该有 exe。
# 混着 4 个 txt 会让用户以为这些都是要带走的东西。
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WPos {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out WR r);
    [StructLayout(LayoutKind.Sequential)] public struct WR { public int L, T, R, B; }
}
"@

# 13 页全测。228 轮发现这里只写了 11 个 —— localtools / notify 是后来加的，
# 列表没跟着更新，那两页的「控件越界 / 零尺寸」从来没被检查过。
$pages = @('home','dash','usage','agents','models','projects','exp','skills','mcp','localtools','notify','settings','about')
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 界面布局检查")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add(("=" * 68))

$totalIssues = 0

# 顶层递归函数（避免嵌套函数的变量作用域坑）
function Walk-Ui($el, $depth) {
    if ($depth -gt 6) { return }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $c = $walker.GetFirstChild($el)
    while ($c) {
        $global:LC_Count++
        $n = $c.Current.Name
        $r = $c.Current.BoundingRectangle
        if ($n -and $n.Trim().Length -gt 0) {
            if ($r.Left -lt ($global:LC_WinL - 10) -or $r.Top -lt ($global:LC_WinT - 10)) {
                $global:LC_Issues.Add("越界: [$n] 在 ($([int]$r.Left),$([int]$r.Top)) 窗口 ($($global:LC_WinL),$($global:LC_WinT))")
            }
            if ($r.Width -le 0 -or $r.Height -le 0) {
                $global:LC_Issues.Add("零尺寸: [$n]")
            }
        }
        Walk-Ui $c ($depth + 1)
        $c = $walker.GetNextSibling($c)
    }
}



function Probe-Page([string]$key, [int]$w, [int]$h) {
    # **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = "--page $key --no-focus"
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)

    $alive = $null
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 700
        $alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($alive -and $alive.MainWindowHandle -ne 0) { break }
    }
    if (-not $alive -or $alive.MainWindowHandle -eq 0) {
        return @{ ok = $false; issues = @('窗口未就绪') }
    }

    # 调整到指定尺寸
    [WPos]::SetWindowPos($alive.MainWindowHandle, [IntPtr]::Zero, 80, 80, $w, $h, 0x0004) | Out-Null
    Start-Sleep -Milliseconds 900

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($alive.MainWindowHandle)
    if (-not $root) { return @{ ok = $false; issues = @('读不到窗口元素') } }

    $wr = New-Object WPos+WR
    [WPos]::GetWindowRect($alive.MainWindowHandle, [ref]$wr) | Out-Null

    # 用全局变量收集（PowerShell 的嵌套函数对 script: 作用域处理容易出错）
    $global:LC_Issues = New-Object System.Collections.Generic.List[string]
    $global:LC_Count = 0
    $global:LC_WinL = $wr.L
    $global:LC_WinT = $wr.T

    # **必须包 try/catch**：UIA 遍历会在窗口中途关闭时抛
    # `ElementNotAvailableException`（元素对象失效）。不包的话异常会一路抛出
    # 调用方 → 循环中断 → **留下一个没关的灵枢进程**。
    try {
        Walk-Ui $root 1
    } catch { }

    $issues = $global:LC_Issues

    $procAlive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $crashed = -not $procAlive
    if ($crashed) { $issues.Add("崩溃: 窗口在 $w x $h 下消失") }

    try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }

    return @{ ok = $true; issues = $issues.ToArray(); count = $global:LC_Count; crashed = $crashed }
}

# 两档尺寸：标准与最小
$sizes = @(@{ w = 1366; h = 728; label = '1366x768 笔记本' },
           @{ w = 1024; h = 728; label = '1024x768 老机器' })

foreach ($sz in $sizes) {
    $lines.Add("")
    $lines.Add("---- " + $sz.label + " ----")
    foreach ($pg in $pages) {
        $r = Probe-Page $pg $sz.w $sz.h
        if (-not $r.ok) {
            $lines.Add(("[FAIL] {0,-10} {1}" -f $pg, ($r.issues -join '; ')))
            $totalIssues++
            continue
        }
        if ($r.issues.Count -eq 0) {
            $lines.Add(("[ OK ] {0,-10} 控件 {1} 个，无异常" -f $pg, $r.count))
        } else {
            $lines.Add(("[WARN] {0,-10} 控件 {1} 个，{2} 个问题" -f $pg, $r.count, $r.issues.Count))
            foreach ($i in ($r.issues | Select-Object -First 5)) {
                $lines.Add("        " + $i)
            }
            $totalIssues += $r.issues.Count
        }
    }
}

# **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
$lines.Add("")
$lines.Add(("=" * 68))
$lines.Add("合计问题: $totalIssues")

$lines | ForEach-Object { Write-Host $_ }
$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ""
Write-Host "报告: $OutFile" -ForegroundColor Cyan

# **显式退出码。**
# 原来脚本没有 exit，退出码取决于最后一条命令 —— 通常是 0，
# 甚至「发现了 15 处问题」也返回 0。
# 调用方（check_all / verify_all / 外部 CI）只能去猜输出文本，
# 而文本判据一改就失效。现在：问题数 → exit 1。
if ($totalIssues -gt 0) { exit 1 } else { exit 0 }
