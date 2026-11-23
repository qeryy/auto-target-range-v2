# 文档一致性检查
#
# 9 份文档里会重复写「exe 多大」「多少行」「多少个文件」。
# 改代码之后很容易只更新一份，其他几份还留着旧数字——
# 到时候读文档的人会看到互相矛盾的数据。
#
# 这个脚本从源码和产物算出真值，再和文档里写的比。

$ErrorActionPreference = 'Stop'
$appRoot = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) } else { (Get-Location).Path }
Set-Location $appRoot

$files = Get-ChildItem 'src' -Recurse -Filter '*.cs'
$truth = @{
    # **必须指定 -Encoding UTF8**。不指定时 PowerShell 5 按 ANSI(GBK) 读，
    # 源码里的中文注释会变乱码、行数被少算（实测 148 行 / 74 文件）。
    # 154 轮发现：doc_check 的真值一直是错的（19114 实际 19262）。
    Lines = (Get-Content ($files | ForEach-Object { $_.FullName }) -Encoding UTF8 | Measure-Object -Line).Lines
    Count = $files.Count
    KB    = [math]::Round((Get-Item 'out\灵枢.exe').Length / 1KB)
}

Write-Host "真值：$($truth.Count) 个文件 · $($truth.Lines) 行 · $($truth.KB) KB" -ForegroundColor Cyan
Write-Host ""

# 只检查「描述当前状态」的那几份；ARCHITECTURE 和扩展指南里有历史值，不查
# README 是用户最先看的那份，225 轮发现它一直没被检查：
# 「75 个文件 · 19460 行」挂了十几轮，实际 84 / 22283。
$checkDocs = @('交付快照.md', '测试覆盖清单.md', '提交前必读.md', '发布说明.md',
                '能力清单.md', 'README.md', '界面清单.md', '开发流程约定.md')
$issues = 0

foreach ($doc in $checkDocs) {
    if (-not (Test-Path $doc)) { continue }
    $c = Get-Content $doc -Raw -Encoding utf8
    $bad = @()

    # 只看「总计 / 代码 / 源文件」这几类表述，避免把分层的数字（比如 Ui 层 7218 行）
    # 误判成总行数。
    $totalPatterns = @(
        '总计[^|\n]*?(\d{4,6})\s*行',
        '代码\s*\|\s*\*\*(\d{4,6})\s*行',
        '\|\s*代码\s*\|\s*\*\*(\d{4,6})'
    )
    foreach ($pat in $totalPatterns) {
        foreach ($m in [regex]::Matches($c, $pat)) {
            $v = [int]$m.Groups[1].Value
            if ($v -ne $truth.Lines) { $bad += "总行数写成 $v（应 $($truth.Lines)）" }
        }
    }

    # exe 大小：只认 1400-5000 区间里明确说「产物 / exe / 单 exe」的
    foreach ($m in [regex]::Matches($c, '(?:产物|exe|单 exe)[^|\n]{0,40}?(\d{3,5})\s*KB')) {
        $v = [int]$m.Groups[1].Value
        if ($v -gt 1400 -and $v -lt 5000 -and $v -ne $truth.KB) {
            $bad += "exe 大小写成 $v KB（应 $($truth.KB)）"
        }
    }


    # ---------- 结构数字：页面数 / 工具数 / agent 数 ----------
    #
    # 界面清单和能力清单里会写「13 个页面」「8 个工具」这类数字。
    # 加一个页面而忘了改文档，读到的人会以为只有 13 个。
    $truthFiles = (Get-ChildItem 'src' -Recurse -Filter '*.cs').Count
$truthDocs = (Get-ChildItem -Filter '*.md' -File).Count
$truthPages = (Select-String -Path 'src/Ui/Program.cs' -Pattern 'PageRegistry\.Register' -Encoding utf8).Count
    $truthTools = (Select-String -Path 'src/Tools/ITool.cs' -Pattern 'reg\.Register' -Encoding utf8).Count
    # 注意：不能用 'Key = "' 计数 —— 构造函数里有 `Key = "";` 的默认值初始化，
# 那会把 agent 数多算一个（实测 10 vs 真实 9）。要求引号里至少一个字母。
$truthAgents = (Select-String -Path 'src/Agents/AgentProfile.cs' -Pattern 'Key = "[a-z_]+"' -Encoding utf8).Count

    $structChecks = @(
        @{ Text = $c; Pattern = '(\d+)\s*个页面'; Truth = $truthPages; Label = '页面数' },
        @{ Text = $c; Pattern = '(\d+)\s*个工具'; Truth = $truthTools; Label = '工具数' },
        @{ Text = $c; Pattern = '(\d+)\s*个 agent'; Truth = $truthAgents; Label = 'agent 数' },
    # 源文件数：之前只查页面/工具/agent，漏了这个 ——
    # 154 轮发现 交付快照 写着「51 个源文件」而实际 74 个，检查从来没报过。
    @{ Text = $c; Pattern = '(\d+)\s*个源文件'; Truth = $truthFiles; Label = '源文件数' },
    @{ Text = $c; Pattern = '(\d+)\s*份文档'; Truth = $truthDocs; Label = '文档数' }
    )
    # 只在「描述当前状态」的文档里核对结构数字。
    # 测试覆盖清单是**历史记录**（「第 48 轮时是 6 个工具」「46 轮里加过 6 个页面」），
    # 按当前值去核对那些历史数字是错的——它们本来就不该等于现在。
    $isHistoryDoc = $doc -match '测试覆盖清单|ARCHITECTURE'
    if (-not $isHistoryDoc) {
        foreach ($sc in $structChecks) {
            foreach ($m in [regex]::Matches($sc.Text, $sc.Pattern)) {
                $v = [int]$m.Groups[1].Value
                if ($v -ne $sc.Truth) { $bad += "$($sc.Label)写成 $v（应 $($sc.Truth)）" }
            }
        }
    }

    # ---------- 交付快照的分模块文件清单 ----------
    #
    # 232 轮发现：`Core/` 那节列了 10 个文件，`AppInfo.cs`（199 轮新增）**整个没列**。
    # 逐文件行数那张表看起来很像"完整的清单"，缺一个也不会有人发现。
    #
    # 现在改成一行「`文件名` 行数 · …」，这里核对**名字集合**是否等于磁盘。
    if ($doc -eq '交付快照.md') {
        foreach ($dir in (Get-ChildItem 'src' -Directory)) {
            if (-not (Get-ChildItem $dir.FullName -Recurse -Filter '*.cs')) { continue }
            $dn = $dir.Name
            $head = [regex]::Match($c, '### `' + [regex]::Escape($dn) + '/` —— [^\n]*\n')
            if (-not $head.Success) {
                $bad += ("缺少 " + $dn + "/ 这一节")
                continue
            }
            $after = $c.Substring($head.Index + $head.Length)
            # **标题下一行是空行**，要跳过空行取第一条非空内容。
            # 第一版直接取紧接的那一行，结果拿到空串、把每个文件都报成「没列出」。
            $listLine = ''
            foreach ($raw in ($after -split "`n")) {
                if ($raw.Trim().Length -gt 0) { $listLine = $raw; break }
            }
            $listed = @([regex]::Matches($listLine, '`([\w.]+\.cs)`') |
                ForEach-Object { $_.Groups[1].Value })
            $onDisk = @(Get-ChildItem $dir.FullName -Recurse -Filter '*.cs' |
                ForEach-Object { $_.Name })
            $missing = @($onDisk | Where-Object { $listed -notcontains $_ })
            $extra = @($listed | Where-Object { $onDisk -notcontains $_ })
            if ($missing.Count -gt 0) {
                $bad += ($dn + "/ 里这些文件没列进交付快照: " + ($missing -join ', '))
            }
            if ($extra.Count -gt 0) {
                $bad += ($dn + "/ 里列了已经不存在的文件: " + ($extra -join ', '))
            }
        }
    }

    # ---------- 界面清单：每个按钮文案都要出现过 ----------
    #
    # 234 轮发现：`界面清单` 里「仪表盘」「用量」两页根本没有刷新按钮，
    # 「经验」页少了「查重」，会话详情只画了 4 个标签（实际 6 个）。
    # 这些都不影响数字检查，只能靠比对文案。
    #
    # **要比 trim 过的** —— 有些标签是拼接用的前缀，比如
    #     Theme.Ghost("↑ 更早的 " + n + " 条已折叠…")
    # 引号里带尾随空格。直接比字符串会永远匹配不上。
    if ($doc -eq '界面清单.md') {
        $labels = @()
        foreach ($cs in (Get-ChildItem 'src/Ui' -Recurse -Filter '*.cs')) {
            $code = Get-Content $cs.FullName -Raw -Encoding UTF8
            foreach ($mm in [regex]::Matches($code,
                'Theme\.(?:Primary|Ghost|DangerBtn|Secondary)\("([^"]+)"')) {
                $labels += $mm.Groups[1].Value.Trim()
            }
        }
        $labels = @($labels | Sort-Object -Unique)
        $undoc = @($labels | Where-Object { $labels.Count -gt 0 -and $c.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
        if ($undoc.Count -eq 0) {
            Write-Host ("        [ OK ] " + $labels.Count + " 种按钮文案都在清单里") -ForegroundColor Green
        } else {
            $bad += ("这些按钮文案没出现在清单里: " + ($undoc -join ' / '))
        }
    }

    $bad = $bad | Select-Object -Unique
    if ($bad.Count -gt 0) {
        Write-Host "[WARN] $doc" -ForegroundColor Yellow
        $bad | ForEach-Object { Write-Host "        $_" -ForegroundColor Yellow }
        $issues += $bad.Count
    } else {
        Write-Host "[ OK ] $doc" -ForegroundColor Green
    }
}

# ---------- 使用说明.md 的准确性 ----------
#
# 这份文档是**给用户看的**，里面的工具清单/配置值必须和代码一致。
# 不然用户按文档找不到工具、或者看到的容量上限是错的。
#
# 工具名从 ITool.cs 的注册列表**动态提取**——加工具时如果忘了写进使用说明，
# 这里会报出来（而不是靠人记得）。
$manual = '使用说明.md'
if (Test-Path $manual) {
    Write-Host ""
    $mBad = @()

    # 1) 已注册的工具名都要在使用说明里出现
    $reg = Get-Content 'src\Tools\ITool.cs' -Raw -Encoding UTF8
    $cls = [regex]::Matches($reg, 'reg\.Register\(new (\w+)\(\)\)') | ForEach-Object { $_.Groups[1].Value }
    $toolNames = @()
    foreach ($c in $cls) {
        $hit = Get-ChildItem 'src\Tools' -Filter '*.cs' | Select-String -Pattern ('class ' + $c + '\b') -Encoding UTF8 | Select-Object -First 1
        if (-not $hit) { continue }
        $nm = Select-String -Path $hit.Path -Pattern 'public string Name \{ get \{ return "([a-z_]+)"; \} \}' -Encoding UTF8 | Select-Object -First 1
        if ($nm) { $toolNames += $nm.Matches[0].Groups[1].Value }
    }
    $mtext = Get-Content $manual -Raw -Encoding UTF8
    # 要求工具**在表格里有一行**（格式 `| ``toolname`` | 说明 |`），
    # 不只是"文档某处提过这个名字"。
    # 第一版只查「出现过」→ 删掉表格行后仍报 OK（因为正文别处也提到了），
    # 那是假阴性。表格行才是「有没有介绍」的判据。
    $notDocumented = @($toolNames | Where-Object {
        $mtext -notmatch ('\|\s*`' + [regex]::Escape($_) + '`\s*\|')
    })
    if ($notDocumented.Count -gt 0) {
        $mBad += "以下工具在使用说明的工具表里没有条目: " + ($notDocumented -join ', ')
    }

    # 2) 日志上限（从代码算，不硬编码）
    $cfg = Get-Content 'src\Core\Config.cs' -Raw -Encoding UTF8
    $keep = if ($cfg -match 'LogKeepFiles = (\d+)') { [int]$Matches[1] } else { 0 }
    $maxMB = 0
    if ($cfg -match 'LogMaxBytes = (\d+)L \* 1024 \* 1024') { $maxMB = [int]$Matches[1] }
    if ($keep -gt 0 -and $mtext -notmatch "最近 $keep 个文件") {
        $mBad += "日志保留份数写成别的（代码是 $keep 份）"
    }
    if ($maxMB -gt 0 -and $mtext -notmatch "不超过 $maxMB ?MB") {
        $mBad += "单份日志上限写成别的（代码是 $maxMB MB）"
    }

    # 3) 工具调用轮数
    $loop = Get-Content 'src\Session\AgentLoop.cs' -Raw -Encoding UTF8
    if ($loop -match 'MaxToolRounds = (\d+)') {
        $rounds = [int]$Matches[1]
        if ($mtext -notmatch "最多 $rounds 轮") { $mBad += "工具轮数写成别的（代码是 $rounds 轮）" }
    }

    # 4) 页面数 + 检查项数
    $pageCount = (Select-String -Path 'src/Ui/Program.cs' -Pattern 'PageRegistry\.Register' -Encoding UTF8).Count
    if ($mtext -notmatch "$pageCount 页") { $mBad += "页面数写成别的（代码是 $pageCount 页）" }

    if ($mBad.Count -gt 0) {
        Write-Host "[WARN] $manual" -ForegroundColor Yellow
        $mBad | ForEach-Object { Write-Host "        $_" -ForegroundColor Yellow }
        $issues += $mBad.Count
    } else {
        Write-Host "[ OK ] $manual（$($toolNames.Count) 个工具 / $pageCount 页 / 配置值一致）" -ForegroundColor Green
    }
} else {
    Write-Host "[WARN] 找不到 使用说明.md" -ForegroundColor Yellow
    $issues++
}

# ---------- 配置对话框的字段必须在使用说明里 ----------
#
# ModelsPage 的两个对话框（供应商 / 模型）字段是代码里 Row("...") 写死的。
# 加了必填项却没写进使用说明，用户按文档操作会卡在「还有个 * 没填」——
# 152 轮就是这么发现漏了 API Key 和 显示名 两个必填项的。
$mdPath = 'src/Ui/Pages/ModelDialogs.cs'
if ((Test-Path $mdPath) -and (Test-Path '使用说明.md')) {
    $dlgCode = Get-Content $mdPath -Raw -Encoding UTF8
    $dlgText = Get-Content '使用说明.md' -Raw -Encoding UTF8
    $fieldNames = @([regex]::Matches($dlgCode, 'Row\("([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value.TrimEnd(' ', '*') } | Sort-Object -Unique)

    Write-Host ""
    Write-Host "  配置对话框字段: $($fieldNames.Count) 个"
    # **要求字段出现在表格行里**（`| 名称 \* | 说明 |` 这种），
    # 不只是"文档某处提过这个词"。
    # 第一版只查「出现过」→ 删掉表格行后仍报 OK，因为正文别处也提到了它
    # （「API Key 存在 secrets.dat 里」）。那是假阴性，和 146 轮图标检查
    # 踩的是同一个坑：**「出现过」不等于「介绍过」**。
    $undoc = @($fieldNames | Where-Object {
        $dlgText -notmatch ('\|\s*' + [regex]::Escape($_) + '[^|\n]*\|')
    })
    if ($undoc.Count -gt 0) {
        Write-Host ("  [FAIL] 这些对话框字段没在使用说明里: " + ($undoc -join ', '))
        Write-Host "         → 在「第 1 步：配一个模型」的表格里补上"
        $issues++
    } else {
        Write-Host "  [ OK ] 对话框字段都在使用说明里"
    }
}

# ---------- 面向用户的文档里，相对链接必须可达 ----------
#
# 图片用 `![]()`，普通链接用 `[]()`。之前只查了图片。
# README 那种「先看哪个」的索引表全是链接，一个失效就断一条路。
# 只查用户会打开的几份 —— ARCHITECTURE 里有大量历史链接，不在这管。
$linkDocs = @('README.md', '使用说明.md', '发布说明.md')
$linkBad = 0
foreach ($ld in $linkDocs) {
    if (-not (Test-Path $ld)) { continue }
    $lt = Get-Content $ld -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($lt, '(?<!\!)\[[^\]]+\]\(([^)]+)\)')) {
        $t = $m.Groups[1].Value
        if ($t -match '^https?://' -or $t -match '^#') { continue }
        if (-not (Test-Path $t)) {
            Write-Host "[WARN] $ld 里的链接失效: $t" -ForegroundColor Yellow
            $linkBad++
        }
    }
}
if ($linkBad -gt 0) { $issues += $linkBad }
else { Write-Host "[ OK ] 用户文档里的相对链接都可达" -ForegroundColor Green }

# ---------- 文档里引用的图片必须真的存在 ----------
#
# 文档里写 `![说明](docs/screenshots/home.png)`，图被删了或改名了，
# Markdown 渲染出来就是一个破图标。这种问题**看一眼文档源码发现不了**，
# 只有实际渲染才知道。这里直接查引用路径。
Write-Host ""
$imgBad = 0
foreach ($doc in $checkDocs + @('使用说明.md')) {
    if (-not (Test-Path $doc)) { continue }
    $text = Get-Content $doc -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, '!\[[^\]]*\]\(([^)]+)\)')) {
        $rel = $m.Groups[1].Value
        if ($rel -match '^https?://') { continue }   # 外链不查
        if (-not (Test-Path $rel)) {
            Write-Host "[WARN] $doc 引用的图片不存在: $rel" -ForegroundColor Yellow
            $imgBad++
        }
    }
}
if ($imgBad -gt 0) { $issues += $imgBad }
else { Write-Host "[ OK ] 文档引用的图片都存在" -ForegroundColor Green }

Write-Host ""
if ($issues -eq 0) {
    Write-Host "文档数字与代码一致" -ForegroundColor Green
} else {
    Write-Host "有 $issues 处需要同步（数字过时或图片缺失）" -ForegroundColor Yellow
}
exit $issues
