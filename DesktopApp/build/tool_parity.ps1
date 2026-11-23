# build/tool_parity.ps1 —— 扩展一致性检查
#
# 名字叫 tool_parity 是因为最早只查工具。现在覆盖**四条不变量**，
# 都是「加/删功能时容易漏、漏了又不报错」的东西：
#
#   1. **agent 白名单里出现的每个工具名，都必须是已注册的工具**
#      —— 违反通常是「删了工具但忘了清白名单」，运行时会静默跳过（不报错，
#         只是那个 agent 少一个能力）。132 轮就是这么漏掉的。
#   2. 每个已注册工具**至少有一个 agent 能用**
#      —— 违反说明「加了工具但没人能用」，等于白加。这里算警告不算失败，
#         因为 master/verifier 这类编排型 agent 本就不带工具。
#   3. **每个注册页面都要有图标**
#      —— PageIcons.Get 查不到就返回空串，所以漏配不报错、只是侧栏少个符号。
#         146 轮真加页面时踩到过。
#   4. **smoke.ps1 覆盖的页面要和 PageRegistry 对齐**
#      —— smoke 的列表是手工维护的，加页面忘了加冒烟项就永远不被测到。
#
# 工具名从各 *Tool.cs 的 `public string Name { get { return "xxx"; } }` 里抽，
# 不硬编码列表——加工具时脚本自动跟。

$ErrorActionPreference = 'Stop'
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

$toolFiles = Get-ChildItem 'src\Tools' -Filter '*Tool.cs' -File
$registered = @()
foreach ($f in $toolFiles) {
    $m = Select-String -Path $f.FullName -Pattern 'public string Name \{ get \{ return "([a-z_]+)"; \} \}' -Encoding UTF8 |
         Select-Object -First 1
    if ($m) { $registered += $m.Matches[0].Groups[1].Value }
}
$registered = @($registered | Sort-Object -Unique)

# 只统计「真的在 ITool.CreateDefault 里注册过」的（防止写了类但忘了注册）
$reg = Get-Content 'src\Tools\ITool.cs' -Raw -Encoding UTF8
$actuallyRegistered = @()
foreach ($name in $registered) {
    # 需要类名 → 通过 Name 反查太绕，直接看 CreateDefault 里 new 了哪些类，
    # 再验证那些类的 Name 值在 $registered 里
    $null = $name
}
# 简化：CreateDefault 里 new 的每个类，都去它文件里取 Name
$classNames = [regex]::Matches($reg, 'reg\.Register\(new (\w+)\(\)\)') | ForEach-Object { $_.Groups[1].Value }
$active = @()
foreach ($cls in $classNames) {
    $f = Get-ChildItem 'src\Tools' -Filter '*.cs' -File |
         Select-String -Pattern ('class ' + $cls + '\b') -Encoding UTF8 | Select-Object -First 1
    if (-not $f) { Write-Host ("  [WARN] 注册了 " + $cls + " 但找不到定义"); continue }
    $line = Select-String -Path $f.Path -Pattern 'public string Name \{ get \{ return "([a-z_]+)"; \} \}' -Encoding UTF8 | Select-Object -First 1
    if ($line) { $active += $line.Matches[0].Groups[1].Value }
    else { Write-Host ("  [WARN] " + $cls + " 没有标准的 Name 属性") }
}
$active = @($active | Sort-Object -Unique)

# agent 白名单里出现的所有工具名
$prof = Get-Content 'src\Agents\AgentProfile.cs' -Raw -Encoding UTF8
$declared = @()
foreach ($m in [regex]::Matches($prof, 'Tools = new\[\] \{([^}]*)\}')) {
    foreach ($t in [regex]::Matches($m.Groups[1].Value, '"([a-z_]+)"')) {
        $declared += $t.Groups[1].Value
    }
}
$declared = @($declared | Sort-Object -Unique)

Write-Host ""
Write-Host "  已注册工具: $($active.Count) 个  [$($active -join ' ')]"
Write-Host "  白名单出现: $($declared.Count) 个"
Write-Host ""

$fail = 0
$missing = @($declared | Where-Object { $active -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Host ("  [FAIL] 白名单里有、但没注册的工具: " + ($missing -join ', '))
    Write-Host "         → 这些名字在运行时会被静默跳过，对应 agent 少了能力"
    $fail++
} else {
    Write-Host "  [ OK ] 白名单里每个工具都已注册"
}

$unused = @($active | Where-Object { $declared -notcontains $_ })
if ($unused.Count -gt 0) {
    Write-Host ("  [WARN] 有工具没有任何 agent 能用: " + ($unused -join ', '))
    Write-Host "         （编排型 agent 不带工具是正常的；但如果这是解题工具就要补白名单）"
} else {
    Write-Host "  [ OK ] 每个工具都有 agent 能用"
}

# ---------- 第三条不变量：每个注册页面都要有图标 ----------
#
# PageIcons.Get 查不到时返回空串（不显示），所以「忘加图标」不会报错、
# 只会在侧栏留下一个没有符号的条目，和其余项摆在一起显得缺一块。
# 第 146 轮真加过一个页面来验：图标那步漏了确实少一个符号。
$prog = Get-Content 'src/Ui/Program.cs' -Raw -Encoding UTF8
$pageClasses = [regex]::Matches($prog, 'PageRegistry\.Register\(new Pages\.(\w+)\(\)\)') |
    ForEach-Object { $_.Groups[1].Value }
$pageIds = @()
foreach ($cls in $pageClasses) {
    $hit = Get-ChildItem 'src/Ui/Pages' -Filter '*.cs' | Select-String -Pattern ('class ' + $cls + '\b') -Encoding UTF8 | Select-Object -First 1
    if (-not $hit) { continue }
    $body = Get-Content $hit.Path -Raw -Encoding UTF8
    # **必须从类定义处开始找**：一个文件里可能有多个类（AboutPage 就在
    # SettingsPage.cs 里）。对整个文件取第一个 Id 会抓到前一个类的，
    # 导致 AboutPage 被误认成 settings —— 那样删掉 about 的图标也查不出来。
    $clsIdx = $body.IndexOf('class ' + $cls)
    if ($clsIdx -lt 0) { continue }
    $slice = $body.Substring($clsIdx)
    $m = [regex]::Match($slice, 'public string Id \{ get \{ return "([a-z_]+)"; \}')
    if ($m.Success) { $pageIds += $m.Groups[1].Value }
}
$iconFile = Get-Content 'src/Ui/IPage.cs' -Raw -Encoding UTF8
$iconIds = @([regex]::Matches($iconFile, '\{ "([a-z_]+)",\s*"\\u') | ForEach-Object { $_.Groups[1].Value })

Write-Host ""
Write-Host "  注册页面: $($pageIds.Count) 个 · 有图标: $($iconIds.Count) 个"
$noIcon = @($pageIds | Where-Object { $iconIds -notcontains $_ })
if ($noIcon.Count -gt 0) {
    Write-Host ("  [FAIL] 这些页面没有图标（侧栏会缺一个符号）: " + ($noIcon -join ', '))
    Write-Host "         → 在 src/Ui/IPage.cs 的 PageIcons.Map 里补一行"
    $fail++
} else {
    Write-Host "  [ OK ] 每个注册页面都有图标"
}

# ---------- 第四条不变量：smoke.ps1 的页面列表要和 PageRegistry 对齐 ----------
#
# smoke.ps1 里的 $pages 是**手工维护**的（每项还要写 expect 期望文字，
# 没法自动生成）。加页面时忘了往这里加一条，新页面就**永远不会被冒烟测到**——
# 不报错、不提示，只是漏测。
# 这条检查保证「注册过的页面至少都在冒烟列表里」。
# （expect 里的期望文字对不对，只能靠人写，这里管不了。）
$smokePath = 'build/smoke.ps1'
if (Test-Path $smokePath) {
    $smokeText = Get-Content $smokePath -Raw -Encoding UTF8
    $smokeKeys = @([regex]::Matches($smokeText, "\{ key = '([a-z_]+)'") | ForEach-Object { $_.Groups[1].Value })
    $smokeKeys = @($smokeKeys | Sort-Object -Unique)

    Write-Host ""
    Write-Host "  smoke 覆盖 $($smokeKeys.Count) 页 · PageRegistry 注册 $($pageIds.Count) 页"

    $notInSmoke = @($pageIds | Where-Object { $smokeKeys -notcontains $_ })
    $extraInSmoke = @($smokeKeys | Where-Object { $pageIds -notcontains $_ })

    if ($notInSmoke.Count -gt 0) {
        Write-Host ("  [FAIL] 这些页面不在 smoke.ps1 里（加了页面但没加冒烟项）: " + ($notInSmoke -join ', '))
        Write-Host "         → 在 build/smoke.ps1 的 pages 数组里补一条（key/name/expect）"
        $fail++
    } elseif ($extraInSmoke.Count -gt 0) {
        Write-Host ("  [FAIL] smoke.ps1 里有已经不存在的页面: " + ($extraInSmoke -join ', '))
        Write-Host "         → 删掉这些条目，否则冒烟会因为找不到按钮而失败"
        $fail++
    } else {
        Write-Host "  [ OK ] smoke 覆盖的页面与注册列表一致"
    }
}

# ---------- 第五条不变量：UI 检查脚本的页面列表要和 PageRegistry 对齐 ----------
#
# smoke / clip / layout / overflow 各自维护一份 $pages。
# `smoke` 那条上面已经查过了，但 **clip / layout / overflow 没有人管** ——
# 228 轮才发现 layout_check 只写了 11 页，`localtools` 和 `notify` 是后来加的，
# 列表没跟着更新，那两页的「控件越界 / 零尺寸」从来没被检查过。
#
# 一个页面「能开」不等于「布局对」。skipped 的那两页等于从来没被看过。
$uiScripts = @(
    @{ Path = 'build/clip_check.ps1';     Label = 'clip_check' },
    @{ Path = 'build/layout_check.ps1';   Label = 'layout_check' },
    @{ Path = 'build/overflow_check.ps1'; Label = 'overflow_check' }
)
foreach ($ui in $uiScripts) {
    if (-not (Test-Path $ui.Path)) { continue }
    $uiText = Get-Content $ui.Path -Raw -Encoding UTF8
    $uiMatch = [regex]::Match($uiText, '\$pages\s*=\s*@\(([^)]*)\)')
    if (-not $uiMatch.Success) {
        Write-Host ("  [WARN] " + $ui.Label + " 里找不到 `$pages 数组，跳过") -ForegroundColor Yellow
        continue
    }
    $uiList = @([regex]::Matches($uiMatch.Groups[1].Value, "'([a-z]+)'") |
        ForEach-Object { $_.Groups[1].Value })
    $uiMiss = @($pageIds | Where-Object { $uiList -notcontains $_ })
    $uiExtra = @($uiList | Where-Object { $pageIds -notcontains $_ })
    if ($uiMiss.Count -gt 0 -or $uiExtra.Count -gt 0) {
        if ($uiMiss.Count -gt 0) {
            Write-Host ("  [FAIL] " + $ui.Label + " 少检查了这些页面: " + ($uiMiss -join ', ')) -ForegroundColor Red
            Write-Host ("         → 在这份脚本的 `$pages 数组里补上") -ForegroundColor Yellow
        }
        if ($uiExtra.Count -gt 0) {
            Write-Host ("  [FAIL] " + $ui.Label + " 里有已经不存在的页面: " + ($uiExtra -join ', ')) -ForegroundColor Red
        }
        $fail++
    } else {
        Write-Host ("  [ OK ] " + $ui.Label + " 覆盖全部 " + $uiList.Count + " 页") -ForegroundColor Green
    }
}

# ---------- 第六条不变量：AgentProfile.Modes 的取值必须合法 ----------
#
# `SupportsMode` 是这么判的：
#     if (string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) return true;
# 所以 `Modes = new[] { "ctf " }`（多一个空格）**不会报错**，
# 只会让那个 agent 在 ctf 模式下永远不出现 —— 这是 132 轮那个洞
# （「4 个 agent 拿不到新工具」）的同一种失效：能力静默消失。
#
# 合法值只有四个：all / src / ctf / competition（RunModeText.ToKey 产出的就是这些）。
$validModes = @('all', 'src', 'ctf', 'competition')
$agentSrc = Get-Content 'src/Agents/AgentProfile.cs' -Raw -Encoding UTF8
$modeDecls = [regex]::Matches($agentSrc, 'Modes\s*=\s*new\[\]\s*\{([^}]*)\}')
$badModes = @()
foreach ($md in $modeDecls) {
    foreach ($lm in [regex]::Matches($md.Groups[1].Value, '"([^"]*)"')) {
        $mv = $lm.Groups[1].Value
        if ($validModes -notcontains $mv) { $badModes += $mv }
    }
}
if ($modeDecls.Count -eq 0) {
    Write-Host "  [WARN] 没在 AgentProfile.cs 里找到 Modes 声明" -ForegroundColor Yellow
} elseif ($badModes.Count -eq 0) {
    Write-Host ("  [ OK ] " + $modeDecls.Count + " 处 Modes 取值都合法（all/src/ctf/competition）") -ForegroundColor Green
} else {
    $uniq = @($badModes | Sort-Object -Unique)
    Write-Host ("  [FAIL] AgentProfile.Modes 里有非法取值: " + ($uniq -join ', ')) -ForegroundColor Red
    Write-Host "         → 合法值只有 all / src / ctf / competition；写错会让该 agent 静默不出现"
    $fail++
}

Write-Host ""
if ($fail -gt 0) { exit 1 } else { exit 0 }