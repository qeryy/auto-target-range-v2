# 文档数字自动同步
#
# 改完代码跑 doc_check 会报「写成 X（应 Y）」。手改容易漏，
# 这个脚本直接按真值替换。

$ErrorActionPreference = 'Stop'
$appRoot = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) } else { (Get-Location).Path }
Set-Location $appRoot

$files = Get-ChildItem 'src' -Recurse -Filter '*.cs'
# **必须 -Encoding UTF8**：不指定时 PS5 按 ANSI(GBK) 读，中文注释变乱码、行数少算。
# 155 轮发现：doc_sync 会把错误的 19114 写回文档，让 doc_check 永远对不上。
$lines = (Get-Content ($files | ForEach-Object { $_.FullName }) -Encoding UTF8 | Measure-Object -Line).Lines
$count = $files.Count
$kb = [math]::Round((Get-Item 'out\灵枢.exe').Length / 1KB)
# 页面数：`PageRegistry.Register` 的行数就是注册页面数。
# **不加这个变量直接写 $truthPages 会替换成空串** —— PowerShell 里未定义的变量是 $null，
# 拼进字符串就是什么都没有。（这轮差点又踩：232 轮加分模块同步时引用了它却没定义。）
$truthPages = (Select-String -Path 'src/Ui/Program.cs' -Pattern 'PageRegistry\.Register' -Encoding UTF8).Count

Write-Host "真值：$count 个文件 · $lines 行 · $kb KB" -ForegroundColor Cyan

# 抓住文档里所有「像总行数」和「像 exe 大小」的数字，替换成真值
$docs = @('交付快照.md', '测试覆盖清单.md', '提交前必读.md', 'README.md')
$changed = 0

foreach ($doc in $docs) {
    if (-not (Test-Path $doc)) { continue }
    $c = Get-Content $doc -Raw -Encoding utf8
    $orig = $c

    # 「总计：**N 个源文件 · M 行 C#**」
    $c = [regex]::Replace($c, '(总计：\*\*\d+ 个源文件 · )\d+( 行 C#\*\*)', "`${1}$lines`${2}")
    # 表格「| 代码 | **N 行** |」
    $c = [regex]::Replace($c, '(\| 代码 \| \*\*)\d+( 行\*\* \|)', "`${1}$lines`${2}")
    # 表格「| 源文件 | **N 个** |」
    $c = [regex]::Replace($c, '(\| 源文件 \| \*\*)\d+( 个\*\* \|)', "`${1}$count`${2}")
    # 「| 产物 | **N KB** 单 exe |」
    $c = [regex]::Replace($c, '(\| 产物 \| \*\*)\d+( KB\*\* 单 exe \|)', "`${1}$kb`${2}")
    # 「| **`灵枢.exe`** | **N KB** |」
    $c = [regex]::Replace($c, '(\| \*\*`灵枢\.exe`\*\* \| \*\*)\d+( KB\*\* \|)', "`${1}$kb`${2}")
    # 「编译产物（xxxx→N KB 那个）」
    $c = [regex]::Replace($c, '(编译产物（\d+→)\d+( KB 那个）)', "`${1}$kb`${2}")
    # 「.cs       N 个   源码」
    $c = [regex]::Replace($c, '(\.cs\s+)\d+( 个\s+源码)', "`${1}$count`${2}")
    # 「总计：**N 个源文件 · M 行 C#**」——原来的规则只换了 M，N 一直不动
    $c = [regex]::Replace($c, '(总计：\*\*)\d+( 个源文件)', "`${1}$count`${2}")
    # 「**N 个源文件**」（单独出现，如 提交前必读 的表格）
    $c = [regex]::Replace($c, '(\| \d+ 个源文件)', "`${1}")   # 占位，下面统一处理
    # 「N 个源文件 + M 份文档 + K 个 build 脚本」——三段一起同步
    $docCount = (Get-ChildItem -Filter '*.md' -File).Count
    $scriptCount = (Get-ChildItem 'build' -Filter '*.ps1' -File).Count
    # 整体替换成「N 个源文件 + M 份文档 + K 个 build 脚本」。
    # 不要用捕获组拼接——第一版错把 ${1}（含「个源文件 +」）替换成了纯数字，
    # 结果写出「75 份文档 + 11 个 build 脚本11」这种鬼东西。
    $c = [regex]::Replace($c,
        '\d+ 个源文件 \+ \d+ 份文档 \+ \d+ 个 build 脚本',
        "$count 个源文件 + $docCount 份文档 + $scriptCount 个 build 脚本")

    # ---- 交付快照的分模块行数（232 轮发现：整节都过时了）----
    #
    # 格式：
    #   ## 四、源码（`src/`，74 个文件 · 19262 行）
    #   ### `Core/` —— 基础（10 个 · 1692 行）
    #
    # 这些数字以前**没有任何脚本管** —— 从 74 个文件一路飘到 84 个文件，
    # 每个模块的「N 个 · M 行」也全是旧的。
    $srcAll = Get-ChildItem 'src' -Recurse -Filter '*.cs'
    $c = [regex]::Replace($c, '(源码（`src/`，)\d+( 个文件 · )\d+( 行）)',
        "`${1}$count`${2}$lines`${3}")
    foreach ($dir in (Get-ChildItem 'src' -Directory)) {
        $df = Get-ChildItem $dir.FullName -Recurse -Filter '*.cs'
        if ($df.Count -eq 0) { continue }
        $dl = (Get-Content ($df | ForEach-Object { $_.FullName }) -Encoding UTF8 |
            Measure-Object -Line).Lines
        $dname = [regex]::Escape($dir.Name + '/')
        $c = [regex]::Replace($c,
            ('(### `' + $dname + '` —— [^（]*（)\d+( 个 · )\d+( 行）)'),
            "`${1}$($df.Count)`${2}$dl`${3}")
    }
    # 「布局检查报告（小屏两档 × N 页）」—— layout_check 现在覆盖 13 页
    $c = [regex]::Replace($c, '(布局检查报告（小屏两档 × )\d+( 页）)', "`${1}$truthPages`${2}")
    # 「逐页自测报告（N 页）」
    $c = [regex]::Replace($c, '(逐页自测报告（)\d+( 页）)', "`${1}$truthPages`${2}")

    # ---- 「八、行数分布」整块重算（232 轮发现整块都是旧数字）----
    #
    # 这一块是 ASCII 柱状图，数字和柱子长度都得跟着变，手工维护必然过期。
    # 整块按真值重算：柱子长度 = 该模块行数 / 最大模块行数 × 30。
    $modRows = @()
    foreach ($dir in (Get-ChildItem 'src' -Directory)) {
        $df = Get-ChildItem $dir.FullName -Recurse -Filter '*.cs'
        if ($df.Count -eq 0) { continue }
        $dl = (Get-Content ($df | ForEach-Object { $_.FullName }) -Encoding UTF8 |
            Measure-Object -Line).Lines
        if ($dl -gt 0) { $modRows += [PSCustomObject]@{ Name = $dir.Name; Lines = $dl } }
    }
    if ($modRows.Count -gt 0) {
        $modRows = @($modRows | Sort-Object Lines -Descending)
        $maxLines = ($modRows | Measure-Object Lines -Maximum).Maximum
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine('```')
        foreach ($mr in $modRows) {
            $barLen = [math]::Max(1, [int][math]::Round($mr.Lines / $maxLines * 30))
            $bar = ([string]'█') * $barLen
            # 用 "0.0" 而不是 Round：Round(8.0,1) 在 PowerShell 里会变成整数字符串 "8"，
            # 和 "46.8" 摆在一列就错开一位。
            $pct = ($mr.Lines / $lines * 100).ToString('0.0')
            [void]$sb.AppendLine(('{0,-12} {1,6}  {2,-30} {3,5}%' -f $mr.Name, $mr.Lines, $bar, $pct))
        }
        [void]$sb.AppendLine(('{0,-12} {1,6}' -f '合计', $lines))
        [void]$sb.Append('```')
        $newBlock = $sb.ToString()
        # 替换「## 八、行数分布」标题之后的那一段 ``` 代码块。
        #
        # 用 IndexOf 手工拼接，不用 [regex]::Replace：
        # 第一版写了 `[regex]::Replace($c, $pat, { … }, 1)` —— **.NET 没有这个重载**
        # （带 count 的只有字符串替换版，没有 MatchEvaluator 版），
        # 结果那句静默什么都没做，doc_sync 还报「文档已是最新」。
        $headMark = '## 八、行数分布'
        $hIdx = $c.IndexOf($headMark)
        if ($hIdx -ge 0) {
            $fenceStart = $c.IndexOf('```', $hIdx)
            if ($fenceStart -ge 0) {
                $fenceEnd = $c.IndexOf('```', $fenceStart + 3)
                if ($fenceEnd -ge 0) {
                    $c = $c.Substring(0, $fenceStart) + $newBlock + $c.Substring($fenceEnd + 3)
                }
            }
        }
    }

    # ---- README.md 的几种格式（225 轮发现：README 从来没被同步过）----
    # 「| 交付前清点 | exe 1876 KB · FileVersion 2.0.0.0 |」
    $c = [regex]::Replace($c, '(\| 交付前清点 \| exe )\d+( KB ·)', "`${1}$kb`${2}")
    # 「| 源码 | 75 个文件 · 19460 行 · 零第三方依赖…|」
    $c = [regex]::Replace($c, '(\| 源码 \| )\d+( 个文件 · )\d+( 行)',
        "`${1}$count`${2}$lines`${3}")
    # 「| exe | 1843 KB |」
    $c = [regex]::Replace($c, '(\| exe \| )\d+( KB)', "`${1}$kb`${2}")
    # 正文里的「1843 KB    ← 双击就能用」和「一个 1843 KB 的 exe」
    # 「out\灵枢.exe        1843 KB    ← 双击就能用」
    # 注意：第一版写成 `(?!\d)(\d+)` —— 那个负向先行断言在数字前**必然失败**，
    # 所以这一行从来没被替换过（doc_check 把它抓出来了）。
    # 反引号只用来转义**批引用**（${1} / ${2}），PowerShell 变量直接写。
    # 第一版写成 "`$kb KB"，反引号把 $ 转义了，README 里真的出现了字面量 $kb。
    $c = [regex]::Replace($c, '(\d+) KB(\s+← 双击就能用)', "$kb KB`${2}")
    $c = [regex]::Replace($c, '(\*\*一个 )\d+( KB 的 exe)', "`${1}$kb`${2}")
    # 「ARCHITECTURE.md（253 KB，按关键词搜）」
    if (Test-Path 'ARCHITECTURE.md') {
        $archKb = [int]([math]::Round((Get-Item 'ARCHITECTURE.md').Length / 1KB))
        $c = [regex]::Replace($c, '(ARCHITECTURE\.md\]\(ARCHITECTURE\.md\)（)\d+( KB，)',
            "`${1}$archKb`${2}")
    }

    # 「DesktopApp/ 下 N 个文件」
    if ($doc -eq '提交前必读.md') {
        $total = (Get-ChildItem 'src' -Recurse -File -Include '*.cs','*.md','*.ps1','*.txt','*.json','*.manifest','*.py' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\out\\' }).Count
        $c = [regex]::Replace($c, '(DesktopApp/ 下 )\d+( 个文件)', "`${1}$total`${2}")
    }

    if ($c -ne $orig) {
        Set-Content -Path $doc -Value $c -NoNewline -Encoding utf8
        Write-Host "[SYNC] $doc" -ForegroundColor Yellow
        $changed++
    } else {
        Write-Host "[ OK ] $doc" -ForegroundColor Green
    }
}

Write-Host ""
if ($changed -eq 0) { Write-Host "文档已是最新" -ForegroundColor Green }
else { Write-Host "已同步 $changed 份文档" -ForegroundColor Yellow }
