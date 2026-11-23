# 灵枢桌面版 —— 逐页自测
#
# 做法：用 --page 参数逐页启动，用 UI Automation 读控件树，检查每页的关键元素
# 是否出现、有没有报错文本。比人工点一遍可靠，也能反复跑。
#
# 用法：powershell -ExecutionPolicy Bypass -File DesktopApp\build\smoke.ps1

[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $Exe) { $Exe = Join-Path $appRoot 'out\灵枢.exe' }
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\smoke-report.txt' }
# 报告放 out\reports\ 子目录：out\ 是产物目录，只该有 exe。
# 混着 4 个 txt 会让用户以为这些都是要带走的东西。
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

if (-not (Test-Path $Exe)) {
    Write-Host "[!] 找不到 exe: $Exe" -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# 每页期望出现的文本片段（任一命中即算通过）
$pages = @(
    @{ key = 'home';     name = '任务';      expect = @('任务', '+ 新任务', '还没有任务') },
    @{ key = 'dash';     name = '仪表盘';    expect = @('仪表盘', '会话总数', '消息总数') },
    @{ key = 'usage';    name = '用量';      expect = @('用量', '累计 Token', '按会话') },
    @{ key = 'agents';   name = '编队';      expect = @('编队', '代号', 'agent') },
    @{ key = 'models';   name = '模型';      expect = @('模型', '供应商', '+ 供应商') },
    @{ key = 'projects'; name = '项目';      expect = @('项目', '+ 新建项目', '会话') },
    @{ key = 'exp';      name = '经验';      expect = @('经验', '提炼', '洞察') },
    @{ key = 'skills';   name = '技能';      expect = @('技能', '领域', '条') },
    @{ key = 'mcp';      name = 'MCP 接入';  expect = @('MCP', '服务器', '传输') },
    @{ key = 'localtools'; name = '本地工具'; expect = @('本地工具', '工具目录', '索引') },
    @{ key = 'notify';   name = '通知';      expect = @('通知', '什么时候通知', '任务完成') },
    @{ key = 'settings'; name = '设置';      expect = @('设置', '数据位置', '备份') },
    @{ key = 'about';    name = '关于';      expect = @('关于', '版本', 'SpiritScout') }
)

function Get-UiTexts([IntPtr]$handle, [int]$depth = 6) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    if (-not $root) { return @() }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $out = New-Object System.Collections.Generic.List[string]

    function Walk($el, $d) {
        if ($d -gt $depth) { return }
        $c = $walker.GetFirstChild($el)
        while ($c) {
            $n = $c.Current.Name
            if ($n) { $out.Add($n) }
            Walk $c ($d + 1)
            $c = $walker.GetNextSibling($c)
        }
    }
    # **必须包 try/catch**：UIA 遍历会在窗口中途关闭时抛
    # `ElementNotAvailableException`（窗口没了，元素对象失效）。
    # 不包的话异常会一路抛出调用方 → 循环中断 → **留下一个没关的灵枢进程**。
    # 包了之后：已经收集到的文字照样返回，冒烟照常判下一步。
    try { Walk $root 1 } catch { }
    return $out
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 逐页自测")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add("exe：" + $Exe)
$lines.Add(("=" * 60))

$pass = 0
$fail = 0
$failNames = @()

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
    $psi.Arguments = "--page " + $pg.key + " --no-focus"
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)

    $ok = $false
    $texts = @()
    # 取不到文本不一定是页面没内容 —— 上一项检查刚 kill 过进程时，
    # UIA 子系统会短暂失灵，整页 readings 全空。这时重启进程重来一次，
    # 别把基础设施的抖动记成产品缺陷（verify_all 串行跑时踩到过）。
    for ($attempt = 0; $attempt -lt 2 -and $texts.Count -eq 0; $attempt++) {
        if ($attempt -gt 0) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
            Start-Sleep -Milliseconds 800
            $proc = [System.Diagnostics.Process]::Start($psi)
        }
        for ($k = 0; $k -lt 12; $k++) {
            Start-Sleep -Milliseconds 900
            $alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
            if (-not $alive -or $alive.MainWindowHandle -eq 0) { continue }
            $texts = Get-UiTexts $alive.MainWindowHandle
            if ($texts.Count -gt 5) { break }
        }
    }

    if ($texts.Count -eq 0) {
        $lines.Add(("[FAIL] {0,-10} {1}  —— 窗口没有内容" -f $pg.key, $pg.name))
        $fail++
        $failNames += $pg.name
    } else {
        $joined = $texts -join ' '
        $hit = 0
        $hitWords = @()
        foreach ($e in $pg.expect) {
            if ($joined -like "*$e*") { $hit++; $hitWords += $e }
        }
        if ($hit -ge 1) {
            $lines.Add(("[ OK ] {0,-10} {1}  —— 命中 {2} 个关键词（{3}）" -f $pg.key, $pg.name, $hit, ($hitWords -join ', ')))
            $pass++
        } else {
            $lines.Add(("[FAIL] {0,-10} {1}  —— 未命中任何关键词，实际文本 {2} 条" -f $pg.key, $pg.name, $texts.Count))
            $lines.Add(("         前几条: " + (($texts | Select-Object -First 5) -join ' | ')))
            $fail++
            $failNames += $pg.name
        }
    }

    try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
}

# **按路径过滤**：只杀这次测试启动的那个 exe。
# 原来的写法是无差别杀掉所有名为「灵枢」的进程 —— 如果你自己正开着窗口
# 跑这个脚本，那个窗口也会被关掉。
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $Exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue

$lines.Add(("=" * 60))
$lines.Add(("通过 {0} / 失败 {1}" -f $pass, $fail))
if ($fail -gt 0) { $lines.Add("失败页面：" + ($failNames -join '、')) }

$lines | ForEach-Object { Write-Host $_ }
$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ""
Write-Host "报告已写入: $OutFile" -ForegroundColor Cyan
# **成功也要显式 exit 0。**
# 原来只写了失败分支，成功时靠「最后一条命令的退出码」——那是隐式约定，
# 调用方（check_all / CI）没法可靠依赖。
if ($fail -gt 0) { exit 1 } else { exit 0 }
