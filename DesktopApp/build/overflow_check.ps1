# 内容溢出检查
#
# 上一轮查的是「单个控件里的文字有没有被裁」，这一轮查的是
# 「整个页面的内容有没有超出窗口」——内容跑到窗口外，用户得滚动才能看到，
# 首屏看不到关键控件就是体验问题。
#
# 做法：拿到每个页面里所有可见控件的包围盒，看最大纵坐标/横坐标有没有
# 明显超过窗口客户区。超了就是「需要滚动」。

[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $Exe) { $Exe = Join-Path $appRoot 'out\灵枢.exe' }
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\overflow-report.txt' }
# 报告放 out\reports\ 子目录：out\ 是产物目录，只该有 exe。
# 混着 4 个 txt 会让用户以为这些都是要带走的东西。
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ovr {
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$pages = @('home','dash','usage','agents','models','projects','exp','skills','mcp','localtools','notify','settings','about')

# **已知会纵向滚动的页面**。
#
# UIA 读不到 WinForms 的 AutoScroll 滚动条（`ControlType.ScrollBar` 不暴露），
# 所以「有滚动条就算正常」这条判断在这里行不通——实测 13 页一个都没识别出来。
# 只能显式声明：这些页面的内容本来就比容器高，靠滚动条看剩下的。
#
# 加新页面时：如果它用了 FlowLayoutPanel / AutoScroll，记得往这里加一行。
$scrollablePages = @('agents')
# 两档尺寸：标准与最小（小屏最容易溢出）
$sizes = @(@{ w = 1400; h = 900; label = '1400x900' },
           @{ w = 1024; h = 728; label = '1024x728' })

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 内容溢出检查")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add(("=" * 66))

$bad = 0

foreach ($sz in $sizes) {
    $lines.Add("")
    $lines.Add("---- 窗口 " + $sz.label + " ----")

    foreach ($pg in $pages) {
        # **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Exe
        $psi.Arguments = "--page $pg --no-focus"
        $psi.UseShellExecute = $false
        $proc = [System.Diagnostics.Process]::Start($psi)

        $alive = $null
        for ($i = 0; $i -lt 12; $i++) {
            Start-Sleep -Milliseconds 700
            $alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
            if ($alive -and $alive.MainWindowHandle -ne 0) { break }
        }
        if (-not $alive -or $alive.MainWindowHandle -eq 0) {
            $lines.Add(("[SKIP] {0,-12} 窗口未就绪" -f $pg))
            continue
        }

        [Ovr]::SetWindowPos($alive.MainWindowHandle, [IntPtr]::Zero, 40, 40, $sz.w, $sz.h, 0x0004) | Out-Null
        Start-Sleep -Milliseconds 800

        # 用 UIA 读到的**实际**窗口矩形作基准。
        #
        # 不能用 GetClientRect + SetWindowPos 的入参：
        # SetWindowPos 传的是物理像素，而 DPI 150% 下 Windows 会把它换算，
        # 所以传 1400x900 实际得到 2100x1350。拿 1400 当基准就会把
        # 本来正常的内容误判成「溢出 700px」。
        $tmpRoot = [System.Windows.Automation.AutomationElement]::FromHandle($alive.MainWindowHandle)
        $cw = [int]$tmpRoot.Current.BoundingRectangle.Width
        $ch = [int]$tmpRoot.Current.BoundingRectangle.Height

        $root = [System.Windows.Automation.AutomationElement]::FromHandle($alive.MainWindowHandle)
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

        # 收集所有控件（用 ArrayList，避开 $script: 作用域的坑——
        # 之前那版函数内写 $script:maxBottom、外面读 $maxBottom，读到的是两个变量，
        # 结果把「没有溢出」的页面误报成溢出 700px）
        $win = $root.Current.BoundingRectangle
        $all = New-Object System.Collections.ArrayList
        $script:__hasScrollBar = $false

        # 页面上有没有滚动条 —— 有的话纵向超出是**正常的滚动内容**，不算溢出。
        # 173 轮编队页改成卡片网格后就被误报过：9 张卡在 1024 宽时换行成 3-4 行，
        # 总高 921 > 窗口 728。FlowLayoutPanel 本来就该滚动，不是布局 bug。
        $hasScrollBar = $false

        function Walk($el, $depth) {
            if ($depth -gt 5) { return }
            $c = $walker.GetFirstChild($el)
            while ($c) {
                $r = $c.Current.BoundingRectangle
                # ScrollBar 控件（AutoScroll 容器会生成这个）
                if ($c.Current.ControlType -eq [System.Windows.Automation.ControlType]::ScrollBar) {
                    $script:__hasScrollBar = $true
                }
                if ($r.Width -gt 2 -and $r.Height -gt 2) {
                    [void]$all.Add([PSCustomObject]@{
                        RelR = [int]($r.Right - $win.Left)
                        RelB = [int]($r.Bottom - $win.Top)
                    })
                }
                Walk $c ($depth + 1)
                $c = $walker.GetNextSibling($c)
            }
        }
        # **必须包 try/catch**：UIA 遍历会在窗口中途关闭时抛
        # `ElementNotAvailableException`（元素对象失效）。不包的话异常会一路抛出
        # 调用方 → 循环中断 → **留下一个没关的灵枢进程**。
        try {
            Walk $root 1
        } catch { }

        $maxRight = 0
        $maxBottom = 0
        if ($all.Count -gt 0) {
            $maxRight = ($all | Measure-Object RelR -Maximum).Maximum
            $maxBottom = ($all | Measure-Object RelB -Maximum).Maximum
        }
        $count = $all.Count

        if ($script:__hasScrollBar) { $hasScrollBar = $true }

        # 容差 8px（两边都是含边框的窗口坐标，只留边框/阴影的余量）
        $overV = $maxBottom - $ch - 8
        $overH = $maxRight - $cw - 8
        # 白名单里的页面（或检测到滚动条）→ 纵向超出算正常滚动内容，只看横向
        $allowScroll = ($scrollablePages -contains $pg) -or $hasScrollBar
        if ($allowScroll -and $overV -gt 0) { $overV = 0 }

        if ($overV -gt 0 -or $overH -gt 0) {
            $lines.Add(("[WARN] {0,-12} 窗口 {1}x{2} · 内容延伸到 {3}x{4}（超出 下{5} 右{6}）" -f
                $pg, $cw, $ch, $maxRight, $maxBottom, $overV, $overH))
            $bad++
        } else {
            $tail = if ($allowScroll) { " · 可滚动（纵向不判溢出）" } else { "" }
            $lines.Add(("[ OK ] {0,-12} 窗口 {1}x{2} · 内容 {3}x{4} · {5} 个控件{6}" -f
                $pg, $cw, $ch, $maxRight, $maxBottom, $count, $tail))
        }

        try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue

$lines.Add("")
$lines.Add(("=" * 66))
$lines.Add("溢出页面数: $bad")

$lines | ForEach-Object { Write-Host $_ }
$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ""
Write-Host "报告: $OutFile" -ForegroundColor Cyan

# **显式退出码。**
# 原来脚本没有 exit，退出码取决于最后一条命令 —— 通常是 0，
# 甚至「发现了 15 处问题」也返回 0。
# 调用方（check_all / verify_all / 外部 CI）只能去猜输出文本，
# 而文本判据一改就失效。现在：溢出页面数 → exit 1。
if ($bad -gt 0) { exit 1 } else { exit 0 }
