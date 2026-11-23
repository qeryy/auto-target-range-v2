# 静态检查：不运行程序就能做的验证
#
# ## 为什么需要
#
# 运行测试要起窗口、点页面，有些时候不方便（比如用户要求「先别测」）。
# 但**编译器本身就是个静态分析器**，加上几条针对性 grep，能覆盖掉一大类问题：
#
#   1. `/warn:4 /warnaserror+` —— 未使用变量、不可达代码、未赋值字段、过时 API
#   2. `async void` —— 除事件处理器外都是坑（异常会被吞掉、无法 await）
#   3. `Application.DoEvents` —— 生产代码里不该有（重入风险）
#   4. `Thread.Sleep` —— UI 线程上会冻界面
#   5. `.Result` / `.Wait()` —— UI 线程上可能死锁
#
# 这些**不需要运行程序**，纯看代码和编译器输出，所以任何时刻都能跑。
#
# ## 用法
#
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\static_check.ps1')))"
#
# 退出码：0 = 全干净；非 0 = 有需要看的项。
#
# ## 它不是运行测试的替代品
#
# 静态检查**证明不了**「点这个按钮会发生什么」。它只能证明
# 「代码在编译期和模式层面没有明显问题」。真正的行为验证还是要跑。

[CmdletBinding()]
param(
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
Set-Location $appRoot
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\static-report.txt' }
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 静态检查")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add("")

$bad = 0

function Section([string]$title) {
    Write-Host ""
    Write-Host ("=" * 66) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ("=" * 66) -ForegroundColor DarkGray
    $lines.Add("")
    $lines.Add("===== $title =====")
}

function Note([string]$s) {
    Write-Host ("  " + $s)
    $lines.Add("  " + $s)
}

# ---------- 1) 警告即错误编译 ----------
Section "1. /warn:4 /warnaserror+ 编译"

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

if (-not (Test-Path $csc)) {
    Note "[SKIP] 找不到 csc.exe"
} else {
    $tmp = Join-Path $appRoot ('_static_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        $out = Join-Path $tmp 'w.exe'
        $skills = (Resolve-Path 'src\Resources\skills.json').Path
        $sources = Get-ChildItem 'src' -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName }
        $cscArgs = @(
            '/nologo', '/target:winexe', '/optimize+', "/out:$out",
            '/warn:4', '/warnaserror+',
            '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Drawing.dll', '/r:System.Net.Http.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Security.dll',
            "/resource:$skills,skills.json"
        ) + $sources

        $r = & $csc @cscArgs 2>&1
        $diags = @($r | Where-Object { $_ -match ':\s*(warning|error)\s+CS' })
        if ($diags.Count -eq 0) {
            Note "[ OK ] 零诊断（/warn:4 全开 + 警告即错误）"
        } else {
            Note "[FAIL] $($diags.Count) 条诊断："
            $diags | Group-Object { ($_ -split 'CS')[1].Substring(0, 4) } |
                Sort-Object Count -Descending | ForEach-Object {
                    Note ("  CS" + $_.Name.Trim() + " ×" + $_.Count)
                }
            $diags | Select-Object -First 15 | ForEach-Object { Note ("    " + $_.Trim()) }
            $bad++
        }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------- 2) async void ----------
Section "2. async void（事件处理器以外都是坑）"
$hits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' | Select-String -Pattern '\basync\s+void\b' -Encoding UTF8)
if ($hits.Count -eq 0) {
    Note "[ OK ] 没有 async void"
} else {
    Note "[FAIL] $($hits.Count) 处 async void："
    $hits | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim()) }
    $bad++
}

# ---------- 3) DoEvents ----------
Section "3. Application.DoEvents（重入风险）"
$hits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' | Select-String -Pattern 'DoEvents' -Encoding UTF8)
if ($hits.Count -eq 0) {
    Note "[ OK ] 生产代码里没有 DoEvents"
} else {
    Note "[FAIL] $($hits.Count) 处："
    $hits | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber) }
    $bad++
}

# ---------- 4) Thread.Sleep ----------
Section "4. Thread.Sleep（UI 线程上会冻界面）"
$hits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' | Select-String -Pattern 'Thread\.Sleep' -Encoding UTF8)
if ($hits.Count -eq 0) {
    Note "[ OK ] 没有 Thread.Sleep"
} else {
    # 不直接判失败：重试退避里的 Sleep 是合理的。列出来让人看一眼在哪。
    Note "[info] $($hits.Count) 处（确认不在高频 UI 路径上）："
    $hits | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim()) }
}

# ---------- 5) .Result / .Wait() ----------
Section "5. .Result / .Wait()（UI 线程死锁风险）"
$hits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' |
    Select-String -Pattern '\.Result\b|\.Wait\(\)' -Encoding UTF8 |
    Where-Object { $_.Line -notmatch 'DialogResult|dlg\.Result|m\.Result\(' })
if ($hits.Count -eq 0) {
    Note "[ OK ] 没有可疑的 .Result / .Wait()"
} else {
    Note "[info] $($hits.Count) 处（确认调用点不在 UI 线程的未完成任务上）："
    $hits | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim()) }
}

# ---------- 6) 绘制路径里的 Font 分配 ----------
Section "6. 绘制路径里 new Font（应缓存）"
#
# Font 在 GDI+ 里不算便宜。`OnPaint` 里每次重绘都 new 一个、用完 Dispose，
# 虽然不漏句柄，但是白白的创建/销毁开销 —— 而字号几乎总是固定的，没理由不缓存。
#
# 判据：出现 `new Font(` 但同一行或上一行不是 `static readonly`。
# （`Theme.FontXxx` 和 `BrandPanel.TitleFont` 都是 static readonly 的，正确。）
# 懒加载属性 `_x ?? (_x = new Font(...))` 同样只分配一次，认；
# Theme.F() 是**工厂**本体（调用方全部走缓存），也认——但要人工确认调用点。
$fontHits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' | Select-String -Pattern 'new Font\(' -Encoding UTF8)
$fontBad = @()
foreach ($h in $fontHits) {
    $line = $h.Line
    if ($line -match '//') { continue }                       # 注释里的示例
    if ($line -match 'static\s+readonly') { continue }        # 缓存字段本体
    if ($line -match '\?\?\s*\(_\w+\s*=\s*new Font') { continue }  # 懒加载缓存属性
    if ($h.Filename -eq 'Theme.cs' -and $line -match 'return new Font\(') { continue }  # F() 工厂本体，调用方已缓存
    if ($line -match '^\s*new Font\(') {
        # 声明跨行：看上一行是不是 static readonly
        $all = Get-Content $h.Path -Encoding UTF8
        $prev = if ($h.LineNumber -ge 2) { $all[$h.LineNumber - 2] } else { '' }
        if ($prev -match 'static\s+readonly') { continue }
    }
    $fontBad += $h
}
if ($fontBad.Count -eq 0) {
    Note "[ OK ] 字体都缓存了（没有在绘制/高频路径里 new Font）"
} else {
    Note "[FAIL] $($fontBad.Count) 处可能在绘制路径里建字体："
    $fontBad | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim()) }
    $bad++
}

# ---------- 7) UI 层的 ConfigureAwait(false) ----------
Section "7. UI 层的 ConfigureAwait(false)（await 后不能碰控件）"
#
# `ConfigureAwait(false)` 之后会回到线程池，再访问控件就会抛
# `InvalidOperationException: 跨线程操作无效`。
# UI 目录里出现它，就是需要人确认的候选：await 之后只碰 POCO 才安全。
$hits = @(Get-ChildItem 'src\Ui' -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
    Select-String -Pattern 'ConfigureAwait\(false\)' -Encoding UTF8)
if ($hits.Count -eq 0) {
    Note "[ OK ] UI 层没有 ConfigureAwait(false)"
} else {
    Note "[info] $($hits.Count) 处（确认 await 之后只碰 POCO、不碰控件）："
    $hits | ForEach-Object {
        Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim())
    }
}

# ---------- 8) Form 窗口圆角 ----------
Section "8. 所有 Form 都做了 Win11 圆角"
#
# 窗口圆角是「界面一致」的一部分：12 个窗口里有一个是直角，
# 用户一眼就能看出它不配套。判据：Form 子类的文件里出现
# `Theme.ApplyWin11` 或 `Win11.RoundCorners`（MainForm 走后者）。
$formHits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' |
    Select-String -Pattern 'class\s+\w+\s*:\s*(System\.Windows\.Forms\.)?Form\b' -Encoding UTF8)
$formBad = @()
foreach ($fh in $formHits) {
    $name = [regex]::Match($fh.Line, 'class\s+(\w+)').Groups[1].Value
    $text = Get-Content $fh.Path -Raw -Encoding UTF8
    if ($text -notmatch 'Theme\.ApplyWin11|Win11\.RoundCorners') {
        $formBad += ($name + "（" + $fh.Filename + ":" + $fh.LineNumber + "）")
    }
}
if ($formBad.Count -eq 0) {
    Note "[ OK ] $($formHits.Count) 个 Form 都做了圆角"
} else {
    Note "[FAIL] $($formBad.Count) 个 Form 没做圆角："
    $formBad | ForEach-Object { Note ("  " + $_) }
    $bad++
}

# 8b) 图标与窗口自适应的收口。
#
# `Theme.ApplyWin11` 现在一次做齐：窗口图标、DPI 缩放、工作区夹紧、屏内定位。
# 不走它的 Form（只有 MainForm，它自己调 Win11.RoundCorners）必须把这几件手动补上，
# 漏掉的结果就是用户看到的又是「图标什么都没有」——上一轮就是这么交付出去的。
$noAdapt = @()
foreach ($fh in $formHits) {
    $name = [regex]::Match($fh.Line, 'class\s+(\w+)').Groups[1].Value
    $text = Get-Content $fh.Path -Raw -Encoding UTF8
    $viaApply = $text -match 'Theme\.ApplyWin11'
    $byHand = ($text -match 'Theme\.AppIcon') -and ($text -match 'Theme\.EnsureOnScreen')
    if (-not ($viaApply -or $byHand)) {
        $noAdapt += ($name + "（" + $fh.Filename + ":" + $fh.LineNumber + "）")
    }
}
if ($noAdapt.Count -eq 0) {
    Note "[ OK ] $($formHits.Count) 个 Form 都收口了窗口图标与分辨率自适应"
} else {
    Note "[FAIL] 既没走 ApplyWin11、也没自己补图标/屏内定位的 Form："
    $noAdapt | ForEach-Object { Note ("  " + $_) }
    $bad++
}

# ---------- 9) UI 一致性 ----------
Section "9. UI 一致性"

# 9a) 每个 Form 都设了 Font（漏了会用系统默认宋体）
$formHits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' |
    Select-String -Pattern 'class\s+\w+\s*:\s*(System\.Windows\.Forms\.)?Form\b' -Encoding UTF8)
$noFont = @()
foreach ($fh in $formHits) {
    $nm = [regex]::Match($fh.Line, 'class\s+(\w+)').Groups[1].Value
    $txt = Get-Content $fh.Path -Raw -Encoding UTF8
    if ($txt -notmatch 'Font\s*=\s*Theme\.Font') { $noFont += ($nm + " @ " + $fh.Filename) }
}
if ($noFont.Count -eq 0) { Note "[ OK ] $($formHits.Count) 个 Form 都设了 Theme 字体" }
else { Note "[FAIL] 没设字体的 Form："; $noFont | ForEach-Object { Note ("  " + $_) }; $bad++ }

# 9b) 每个 TabPage 都设了 BackColor（漏了会露系统灰）
$tabHits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' | Select-String -Pattern 'new TabPage\(' -Encoding UTF8)
$noBg = @($tabHits | Where-Object { $_.Line -notmatch 'BackColor' })
if ($noBg.Count -eq 0) { Note "[ OK ] $($tabHits.Count) 个 TabPage 都设了底色" }
else { Note "[FAIL] 没设底色的 TabPage："; $noBg | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber) }; $bad++ }

# 9c) 每个页面视图都用了 Theme.PageHeader（否则标题风格不统一）
$pageFiles = @(Get-ChildItem 'src\Ui\Pages' -Filter '*Page.cs' -ErrorAction SilentlyContinue)
$noHead = @()
foreach ($pf in $pageFiles) {
    $txt = Get-Content $pf.FullName -Raw -Encoding UTF8
    if ($txt -notmatch 'public Control CreateView\(') { continue }
    # 外壳页不是「一页」，是「一页的壳」——页头在二级页自己里面，
    # 壳里没有也不需要 PageHeader。
    if ($pf.Name -eq 'SettingsShellPage.cs') { continue }
    if ($txt -notmatch 'Theme\.PageHeader') { $noHead += $pf.Name }
}
if ($noHead.Count -eq 0) { Note "[ OK ] $($pageFiles.Count) 个页面都用了 Theme.PageHeader" }
else { Note "[FAIL] 没用 PageHeader 的页面："; $noHead | ForEach-Object { Note ("  " + $_) }; $bad++ }

# 9d) DataTable 的空状态（有歧义，只列候选）
#
# 空表格一定要「有话说」，但承载方式有两种：
#   · 直接设 `EmptyText`（DataTable 自带）
#   · 配一个 hint Label，刷新时改它的 Text（SessionTabs 那五个就是这样）
#   · 或者区块标题本身就说明了用途（McpPage 的「测试后显示对方暴露的工具」）
# 静态判据分不清第三种，所以只列出来让人看，不判失败。
$gridHits = @(Get-ChildItem 'src' -Recurse -Filter '*.cs' |
    Select-String -Pattern 'new (Widgets\.)?DataTable\b' -Encoding UTF8)
$noEmpty = @()
foreach ($gh in $gridHits) {
    $v = [regex]::Match($gh.Line, '(\w+)\s*=\s*new').Groups[1].Value
    if (-not $v) { continue }
    $txt = Get-Content $gh.Path -Raw -Encoding UTF8
    if ($txt -notmatch ([regex]::Escape($v) + '\.EmptyText')) {
        $noEmpty += ($v + " @ " + $gh.Filename + ":" + $gh.LineNumber)
    }
}
if ($noEmpty.Count -eq 0) {
    Note "[ OK ] $($gridHits.Count) 个表格都设了 EmptyText"
} else {
    Note "[info] $($noEmpty.Count)/$($gridHits.Count) 个表格没设 EmptyText（确认靠 hint 或区块标题兜底）："
    $noEmpty | ForEach-Object { Note ("  " + $_) }
}

# 9e) 异步事件处理器的异常出口（有歧义，只列候选）
#
# 两种合规写法：
#   · `+= (s,e) => UiSafety.Fire(XxxAsync(), "说明")`  ← 失败时弹友好提示
#   · `+= async (s,e) => await XxxAsync()`             ← 仅当方法内部自带 try/catch
# 静态判据分不清「方法内部有没有 try/catch」，所以只列 `async (s` 的那些让人确认。
$asyncHandlers = @(Get-ChildItem 'src\Ui' -Recurse -Filter '*.cs' |
    Select-String -Pattern '\+=\s*async\s*\(s' -Encoding UTF8)
if ($asyncHandlers.Count -eq 0) {
    Note "[ OK ] 异步事件处理器全部走 UiSafety.Fire"
} else {
    Note "[info] $($asyncHandlers.Count) 处事件处理器直接 await（确认目标方法内部有 try/catch）："
    $asyncHandlers | ForEach-Object {
        Note ("  " + $_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim())
    }
}

# 9f) 页面里的 MessageBox 必须传 owner
#
# 不传 owner 的消息框可能跑到主窗口**后面**（主窗口置顶 / 多显示器时），
# 用户会以为「点了没反应」。
#
# 只查 `src\Ui\Pages`：那里每一处都在按钮回调里、宿Form 一定存在，没有例外。
# `Program.cs`（启动失败 / 全局崩溃，Form 还不存在）和 `UiSafety.cs`
# （线程池线程，拿不到 owner）在 Pages 之外，所以不受这条约束。
$msgHits = @(Get-ChildItem 'src\Ui\Pages' -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
    Select-String -Pattern 'MessageBox\.Show\(' -Encoding UTF8)
$msgBad = @()
foreach ($mh in $msgHits) {
    $all = Get-Content $mh.Path -Encoding UTF8
    $two = $mh.Line + ' ' + $(if ($mh.LineNumber -lt $all.Count) { $all[$mh.LineNumber] } else { '' })
    if ($two -notmatch 'MessageBox\.Show\(\s*(this|Shell\(\)|FindShell\(\)|ShellForm\(\)|ShellOf\(\)|FindForm\(\)|owner|Shell)') {
        $msgBad += ($mh.Filename + ":" + $mh.LineNumber + "  " + $mh.Line.Trim())
    }
}
if ($msgBad.Count -eq 0) {
    Note "[ OK ] $($msgHits.Count) 处 MessageBox 都传了 owner"
} else {
    Note "[FAIL] $($msgBad.Count) 处没传 owner（消息框可能跑到主窗口后面）："
    $msgBad | ForEach-Object { Note ("  " + $_) }
    $bad++
}

# 9g) 确认框走统一封装（只列候选）
# 排除 UiSafety.cs：那里是确认封装的**实现**，它当然得调 MessageBox
$ynHits = @(Get-ChildItem 'src\Ui' -Recurse -Filter '*.cs' |
    Where-Object { $_.Name -ne 'UiSafety.cs' } |
    Select-String -Pattern 'MessageBoxButtons\.YesNo' -Encoding UTF8)
if ($ynHits.Count -eq 0) {
    Note "[ OK ] 所有确认框都走 UiSafety.Confirm（没有裸 YesNo）"
} else {
    Note "[info] $($ynHits.Count) 处裸用 YesNo（确认标题/按钮文案可能不一致）："
    $ynHits | ForEach-Object { Note ("  " + $_.Filename + ":" + $_.LineNumber) }
}

# ---------- 10) 死代码 ----------
Section "10. 私有方法有没有没人用的"
#
# `csc` 不警告未使用的 private 方法（字段会警告 CS0169，方法不会）。
#
# 判据要防一个坑：**委托引用不带括号**。
#   CellMouseEnter += DataTable_CellMouseEnter;   ← 这里没有 `(`
# 所以只看「名字加括号出现过几次」会把所有事件处理器都误判成死代码。
# 这里同时数「加括号」和「不加括号」两种出现，两种都只有定义那一处才算死。
$allFiles = @(Get-ChildItem 'src' -Recurse -Filter '*.cs')
$allText = @{}
foreach ($f in $allFiles) { $allText[$f.FullName] = (Get-Content $f.FullName -Raw -Encoding UTF8) }

$defPat = [regex]'(?m)^\s*private\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\.\s]+?\s+(\w+)\s*\('
$keyword = @('if','for','while','switch','return','using','lock','catch','try')

$defs = @()
foreach ($kv in $allText.GetEnumerator()) {
    foreach ($m in $defPat.Matches($kv.Value)) {
        $nm = $m.Groups[1].Value
        if ($keyword -contains $nm) { continue }
        $defs += [PSCustomObject]@{ File = [System.IO.Path]::GetFileName($kv.Key); Name = $nm }
    }
}

$dead = @()
foreach ($d in $defs) {
    $withParen = 0; $bare = 0
    $callPat = [regex]('\b' + [regex]::Escape($d.Name) + '\s*\(')
    $barePat = [regex]('\b' + [regex]::Escape($d.Name) + '\b')
    foreach ($txt in $allText.Values) {
        $withParen += $callPat.Matches($txt).Count
        $bare += $barePat.Matches($txt).Count
    }
    # 定义本身贡献 1 个 withParen + 1 个 bare。
    # 没有任何调用（withParen==1）且没有任何其他引用（bare==1）→ 疑似死代码。
    if ($withParen -le 1 -and $bare -le 1) { $dead += ($d.Name + " @ " + $d.File) }
}
if ($dead.Count -eq 0) {
    Note "[ OK ] $($defs.Count) 个私有方法都有引用"
} else {
    Note "[info] $($defs.Count) 个私有方法里，$($dead.Count) 个可能没人用（确认不是反射/字符串调用）："
    $dead | ForEach-Object { Note ("  " + $_) }
}

# ---------- 汇总 ----------
Write-Host ""
Write-Host ("=" * 66) -ForegroundColor DarkGray
if ($bad -eq 0) {
    Write-Host "  静态检查干净（这不等于「跑起来对」——只说明编译期和模式层面没问题）" -ForegroundColor Green
    $lines.Add("")
    $lines.Add("静态检查干净")
} else {
    Write-Host "  $bad 类问题需要处理" -ForegroundColor Red
    $lines.Add("")
    $lines.Add("$bad 类问题需要处理")
}

$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host "  报告: $OutFile" -ForegroundColor Cyan
if ($bad -ne 0) { exit 1 }
exit 0
