# build/web_parity.ps1 —— 功能面对照：web 版入口 ⊆ 桌面版页面
#
# web 版结构（实测 static/index.html）：
#   侧栏一级导航 6：home dash usage agents projects exp
#   设置二级分类 6：about localtools mcp models notify skill
#   合计 12 个功能区块；web 把后 6 类收在「设置」外壳里。
#
# 桌面版 13 页 = 12 区块 + SettingsPage（偏好本体）。
# 差异只在层级（web 藏二级、桌面提到侧栏），本脚本核对**集合相等**。
# web 侧从 HTML 现场提取——web 加了区块这边自动跟。
#
# ## 这个检查能证明什么、不能证明什么
#
# **能证明**：
#   · web 的每个功能区块，桌面上都有一个对应页面（集合包含）
#   · 每个桌面页面的 CreateView 里**真的建了控件**（不是只有标题的空壳）
#
# **不能证明**：
#   · 功能等价。页面存在 ≠ 功能相同。
#
# 真正的功能等价要逐项比「这个按钮点了做什么、这个字段校验什么」，
# 而 web 版的功能大多在 `static/js/*.js` 里动态构建 DOM，静态 HTML 里
# 只有少数按钮（实测 index.html 里 12 个 section 加起来才 8 个静态按钮）。
# 想自动比对得解析 JS 的 DOM 构建逻辑——那已经不是「检查」而是「重写」了。
#
# 所以这里的说法要准确：**结构面覆盖**，不是功能面等价。
# 功能等价的证据在 `e2e_check`（68 项断言，含真实 AgentLoop）和
# `构建/tests` 里的工具测试，不在这个脚本里。
#
# ## 加了一道「空壳页」检查
#
# 只验 id 集合有个漏洞：页面注册了但 CreateView 里什么都没建，集合照样相等。
# 这里顺带数一下每页 CreateView 里的控件创建调用数（实测最少 8、中位 11），
# 阈值定 4——留一半余量，同时能抓住「只剩一个标题」的退化。
param([switch]$Strict)
$ErrorActionPreference = "Stop"
# $PSScriptRoot 只在「从文件运行」时有值。
# check_all 用 [scriptblock]::Create + & 调用脚本块，那时它是空的 ——
# Split-Path 收到空串会直接抛错（下面就是 -Stop），脚本当场退出。
# 更糟的是：脚本没输出 FAIL 字样，check_all 看不出它崩了，反而判「通过」。
# 2026-11-22 发现 web_parity / tool_parity 一直是这样假通过的。
if ($PSScriptRoot) {
    Set-Location (Split-Path $PSScriptRoot -Parent)
} elseif ($MyInvocation.MyCommand.Path) {
    Set-Location (Split-Path (Split-Path $MyInvocation.MyCommand.Path) -Parent)
}
# 两者都空（脚本块调用）：留在当前目录 —— check_all 已把 WorkingDirectory 设成 appRoot
$web = Join-Path (Split-Path (Get-Location) -Parent) 'SpiritScout'

$idx = Join-Path $web 'src\spiritscout\web\static\index.html'
if (-not (Test-Path $idx)) {
    Write-Host "[ SKIP ] 找不到 web 版 index.html，跳过对照"
    exit 0
}
$html = Get-Content $idx -Raw -Encoding UTF8
$nav = @([regex]::Matches($html, 'data-nav="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$set = @([regex]::Matches($html, 'data-set="([^"]+)"')   | ForEach-Object { $_.Groups[1].Value })
$alias = @{ skill = 'skills' }
$webNorm = @($nav + $set | ForEach-Object { if ($alias.ContainsKey($_)) { $alias[$_] } else { $_ } } | Where-Object { $_ }) | Sort-Object -Unique

$prog = Get-Content 'src\Ui\Program.cs' -Raw -Encoding UTF8
$pages = @([regex]::Matches($prog, 'PageRegistry\.Register\(new Pages\.(\w+)\(\)\)') | ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique

$ids = @()
foreach ($cls in $pages) {
    # 类不保证和文件同名（AboutPage 写在 SettingsPage.cs），全局搜类定义
    $hit = Get-ChildItem 'src\Ui\Pages' -Filter '*.cs' |
        Select-String -Pattern ('sealed class ' + $cls + '\b') -Encoding UTF8 | Select-Object -First 1
    if (-not $hit) { Write-Host ('[WARN] 页面类 ' + $cls + ' 找不到定义'); continue }
    $lines = Get-Content $hit.Path -Encoding UTF8
    $lo = $hit.LineNumber - 1
    # 窗口按**类结构**算：从类声明往下找到下一个 class 为止，最多 200 行兜底。
    # 原来是写死 30 行 —— SettingsShellPage 的 Id 在 class 之后第 34 行、
    # NotifyPage 在第 130 行，都在窗口外，于是被判「没有 Id 属性」。
    # 2026-11-22 修好 web_parity 假通过之后，这个 WARN 才第一次露出来。
    $scanEnd = [Math]::Min($hit.LineNumber + 200, $lines.Count)
    $nextCls = -1
    for ($k = $hit.LineNumber; $k -lt $scanEnd; $k++) {
        if ($lines[$k] -match '^\s*(sealed |public |internal |abstract )?class\s') { $nextCls = $k; break }
    }
    $hi = if ($nextCls -gt 0) { $nextCls - 1 } else { $scanEnd - 1 }
    if ($hi -lt $lo) { $hi = [Math]::Min($lo + 30, $lines.Count - 1) }
    $body = $lines[$lo..$hi] -join "`n"
    $m = [regex]::Match($body, 'public string Id \{ get \{ return "([^"]+)"')
    if ($m.Success) { $ids += $m.Groups[1].Value } else { Write-Host ('[WARN] ' + $cls + ' 没有 Id 属性') }
}
$ids = @($ids | Sort-Object -Unique)

$missing = @($webNorm | Where-Object { $ids -notcontains $_ })
$extra   = @($ids | Where-Object { $webNorm -notcontains $_ })

# ---------- 空壳页检查：CreateView 里必须真的建东西 ----------
#
# 正则覆盖两种写法：`new Xxx(...)` 和 `Theme.Primary/Ghost/Input/PageHeader(...)`。
# 不追求精确计数，只判断「多不多于 4」。
$ctrlPattern = 'New\s*\(\s*(Button|DataTable|TextBox|Label|Panel|ComboBox|Card|NavItem|Switch|FlatButton)|new\s+(Widgets\.)?(FlatButton|DataTable|TextBox|Label|Panel|ComboBox|Card|Switch)|Theme\.(Primary|Ghost|DangerBtn|Input|PageHeader|Button)\b'
$hollow = @()
$ctrlCounts = @{}
foreach ($cls in $pages) {
    $hit = Get-ChildItem 'src\Ui\Pages' -Filter '*.cs' |
        Select-String -Pattern ('sealed class ' + $cls + '\b') -Encoding UTF8 | Select-Object -First 1
    if (-not $hit) { continue }
    $all = Get-Content $hit.Path -Raw -Encoding UTF8
    $startIdx = $all.IndexOf('public Control CreateView()')
    if ($startIdx -lt 0) { continue }
    # 截到下一个同类成员定义
    $rest = $all.Substring($startIdx)
    $mNext = [regex]::Match($rest.Substring(10), '\n\s{8}(private|public)\s')
    $body = if ($mNext.Success) { $rest.Substring(0, 10 + $mNext.Index) } else { $rest }
    $n = ([regex]::Matches($body, $ctrlPattern)).Count
    $ctrlCounts[$cls] = $n
    # 外壳页不是空壳：它只建壳（nav + body），内容是二级页装的。
    if ($cls -eq 'SettingsShellPage') { continue }
    if ($n -lt 4) { $hollow += ($cls + '(' + $n + ')') }
}

Write-Host ""
Write-Host "===== web 功能面 vs 桌面页面 ====="
Write-Host ('  web 区块 : ' + $webNorm.Count + '  [' + ($webNorm -join ' ') + ']')
Write-Host ('  桌面页面 : ' + $ids.Count + '  [' + ($ids -join ' ') + ']')
Write-Host ('  控件创建 : ' + (($ctrlCounts.GetEnumerator() | Sort-Object Name |
    ForEach-Object { $_.Key + '=' + $_.Value }) -join ' '))
if ($missing.Count -gt 0) {
    Write-Host ('  [FAIL] web 有而桌面缺: ' + ($missing -join ' '))
    exit 1
}
Write-Host "  [ OK ] web 的全部功能区块桌面都有对应页面"
if ($extra.Count -gt 0) {
    Write-Host ('  [info] 桌面独有（层级差异，允许）: ' + ($extra -join ' '))
}
if ($hollow.Count -gt 0) {
    Write-Host ('  [FAIL] 空壳页（CreateView 里控件不足 4 个）: ' + ($hollow -join ' '))
    exit 1
}
Write-Host "  [ OK ] 每个页面都真的建了控件（无空壳页）"
exit 0
