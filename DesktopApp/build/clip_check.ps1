# 控件文字裁切检查
#
# 「界面好不好看」我判断不了，但「文字有没有被裁掉」是能量的：
# 拿 UI Automation 读到每个控件的 BoundingRectangle 和 Name，
# 再按控件实际字体量一遍文字宽度，宽度不够就是被截断了。
#
# 这比截图分析可靠——截图要 OCR，还受抗锯齿干扰。

[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $Exe) { $Exe = Join-Path $appRoot 'out\灵枢.exe' }
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\clip-report.txt' }
# 报告放 out\reports\ 子目录：out\ 是产物目录，只该有 exe。
# 混着 4 个 txt 会让用户以为这些都是要带走的东西。
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$pages = @('home','dash','usage','agents','models','projects','exp','skills','mcp','localtools','notify','settings','about')
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 文字裁切检查")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add(("=" * 66))

function Measure-Text([string]$text, [System.Drawing.Font]$font) {
    # 用与界面一致的测量方式（TextRenderer 带 NoPadding，接近实际渲染）
    $sz = [System.Windows.Forms.TextRenderer]::MeasureText(
        $text, $font,
        (New-Object System.Drawing.Size(10000, 1000)),
        [System.Windows.Forms.TextFormatFlags]::NoPadding)
    return $sz.Width
}

# 界面上几种典型字体（按 Theme.cs 的定义）
$fontBody = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.5)
$fontSmall = New-Object System.Drawing.Font('Microsoft YaHei UI', 8.5)
$fontTiny = New-Object System.Drawing.Font('Microsoft YaHei UI', 8)

$totalClipped = 0
$totalChecked = 0

foreach ($pg in $pages) {
    # **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = "--page $pg --no-focus"
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)

    $alive = $null
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 800
        $alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($alive -and $alive.MainWindowHandle -ne 0) { break }
    }
    if (-not $alive -or $alive.MainWindowHandle -eq 0) {
        $lines.Add(("[SKIP] {0,-12} 窗口未就绪" -f $pg))
        continue
    }

    # **句柄出现 ≠ 界面建完**。DPI 缩放后 BuildShell+ScaleTree 要 1~2 秒，
    # 立刻遍历拿到的是空树 —— 之前所有页都「检查 0 个控件」就是这么来的。
    # 等稳定，且一个控件都没数到时再宽限 2 秒重试一次。
    Start-Sleep -Milliseconds 1800

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($alive.MainWindowHandle)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    # **统一走 $script:**。param() 脚本体在 `-Command scriptblock` 下是函数作用域，
    # 函数里的 $script:checked 和主体的 $checked 会是两个变量——
    # 之前「实际数到 31、报告写 0」就是这么来的。
    $script:clipped = New-Object System.Collections.Generic.List[string]
    $script:checked = 0

    function Walk($el, $depth) {
        if ($depth -gt 5) { return }
        $c = $walker.GetFirstChild($el)
        while ($c) {
            $n = $c.Current.Name
            $r = $c.Current.BoundingRectangle
            if ($n -and $n.Trim().Length -gt 1 -and $r.Width -gt 5) {
                $script:checked++
                # 用一个中等字体量（实际字体多变，这里取保守值）
                $need = Measure-Text $n $fontSmall
                # 控件宽度明显小于文字所需 → 大概率被裁
                if ($need -gt ($r.Width + 12) -and $n.Length -gt 4) {
                    $script:clipped.Add(("{0} 需 {1}px / 实 {2}px" -f
                        ($(if ($n.Length -gt 26) { $n.Substring(0,26) + '…' } else { $n })), $need, [int]$r.Width))
                }
            }
            Walk $c ($depth + 1)
            $c = $walker.GetNextSibling($c)
        }
    }

    # **必须包 try/catch**：UIA 遍历会在窗口中途关闭时抛
    # `ElementNotAvailableException`（元素对象失效）。不包的话异常会一路抛出
    # 调用方 → 循环中断 → **留下一个没关的灵枢进程**。
    # 但异常不能全吞——之前「0 个控件」的静默空转就是这么藏住的。
    $walkErr = ''
    try {
        Walk $root 1
    } catch { $walkErr = $_.Exception.Message }

    # 一个都没数到且没有异常 → 多半是树还没建完，宽限 2 秒重试一次
    if ($script:checked -eq 0 -and -not $walkErr) {
        Start-Sleep -Seconds 2
        $script:checked = 0
        $script:clipped = New-Object System.Collections.Generic.List[string]
        try { Walk $root 1 } catch { $walkErr = $_.Exception.Message }
    }
    if ($walkErr) { $lines.Add(("[WARN] {0,-12} UIA 遍历中断：{1}" -f $pg, $walkErr)) }

    $totalChecked += $script:checked
    if ($script:clipped.Count -eq 0) {
        $lines.Add(("[ OK ] {0,-12} 检查 {1} 个文字控件，无裁切" -f $pg, $script:checked))
    } else {
        $lines.Add(("[WARN] {0,-12} 检查 {1} 个，{2} 处可能被裁" -f $pg, $script:checked, $script:clipped.Count))
        foreach ($x in ($script:clipped | Select-Object -First 6)) {
            $lines.Add("        " + $x)
        }
        $totalClipped += $script:clipped.Count
    }

    try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
}

# **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue

$lines.Add("")
$lines.Add(("=" * 66))
$lines.Add("检查 $totalChecked 个文字控件，$totalClipped 处可能被裁")

# **0 个控件 = 检查本身失效**，不是「全过」。
# 锁屏/无桌面会话时 UIA 拿到的 BoundingRectangle 全是空矩形，
# 一个控件都数不出来 —— 这时报「无裁切」是假绿，必须显式 WARN。
if ($totalChecked -eq 0) {
    $lines.Add("[WARN] UIA 未取到任何文字控件（屏幕锁定或窗口不可见？），本次检查无效")
}

$lines | ForEach-Object { Write-Host $_ }
$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ""
Write-Host "报告: $OutFile" -ForegroundColor Cyan

# **显式退出码。**
# 原来脚本没有 exit，退出码取决于最后一条命令 —— 通常是 0，
# 甚至「发现了 15 处问题」也返回 0。
# 调用方（check_all / verify_all / 外部 CI）只能去猜输出文本，
# 而文本判据一改就失效。现在：被裁处数 → exit 1；空转 → exit 2。
if ($totalClipped -gt 0) { exit 1 }
if ($totalChecked -eq 0) { exit 2 }
exit 0
