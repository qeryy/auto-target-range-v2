# 会话端到端（真机 UI 链路）
#
# ## 为什么需要这个
#
# `e2e_check` 证明的是 **AgentLoop 逻辑层**：假服务 → 协议解析 → 工具分发 →
# 结果回传 → 循环 → 落盘，141 项断言。但它不起界面，`SessionView` 那条
# 「输入框 → 发送按钮 → SendAsync」的路一段都没走。
#
# 这个脚本补的就是那段：真机开 exe、打开会话、**自动发一条**，然后只判结果
# （会话文件里有没有 user + assistant 两条消息、阶段是否到 Completed）。
#
# ## 为什么用 --send-text 而不是 UIA 点按钮
#
# 实测这个程序的 UIA 树只暴露到容器层（43 个元素全是 Pane），
# `DarkTextBox` / `FlatButton` 虽然是标准 TextBox/Button 派生，
# 但拿不到 Edit/Button 元素，ValuePattern、InvokePattern 都用不上。
# 坐标点击又怕分辨率变化失效。
#
# 所以走 `--send-text`：钩子发消息，脚本判结果，不碰界面细节。
#
# ## 用法
#
#   powershell -ExecutionPolicy Bypass -File build\session_ui_check.ps1
#
# 退出码：0 = 全过，1 = 有失败项。

[CmdletBinding()]
param(
    [string]$OutFile = '',
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
$exe = Join-Path $appRoot 'out\灵枢.exe'
$dataRoot = Join-Path $appRoot 'out\data'
$sessDir = Join-Path $dataRoot 'sessions'
$sessId = 'sess-uicheck'
$sessFile = Join-Path $sessDir ($sessId + '.json')
$reportDir = Join-Path $appRoot 'out\reports'
if (-not $OutFile) { $OutFile = Join-Path $reportDir 'session-ui-report.txt' }
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
New-Item -ItemType Directory -Force -Path $sessDir | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host "找不到 exe：$exe" -ForegroundColor Red
    Write-Host "先编译：powershell -File build\build.ps1" -ForegroundColor Yellow
    exit 2
}

# 假服务的固定回复（和 build\fake_llm.py 里 REPLY 一致）
$expectReply = '收到，这是本地假模型的回复'
$sendText = '自检：跑一遍会话链路'

$issues = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 会话端到端（真机 UI）")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add(("=" * 60))

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { $lines.Add("  [ OK ] $label" + $(if ($detail) { "  $detail" } else { "" })) }
    else {
        $lines.Add("  [FAIL] $label" + $(if ($detail) { "  $detail" } else { "" }))
        $issues.Add("$label $(if ($detail) { $detail } else { '' })")
    }
}

# ---------- 1. 假 LLM 服务 ----------
$svcProc = $null
$svcStarted = $false
try {
    $probe = Invoke-WebRequest -Uri 'http://127.0.0.1:8000/v1/models' -TimeoutSec 3 -UseBasicParsing
    if ($probe.StatusCode -eq 200) { $svcStarted = $true }
} catch { }

if (-not $svcStarted) {
    $py = Join-Path $appRoot '..\SpiritScout\.venv\Scripts\python.exe'
    $fakePy = Join-Path $buildDir 'fake_llm.py'
    if (-not (Test-Path $py) -or -not (Test-Path $fakePy)) {
        Write-Host "缺假服务依赖：python=$py  script=$fakePy" -ForegroundColor Red
        exit 2
    }
    $svcProc = Start-Process -FilePath $py -ArgumentList '-X', 'utf8', $fakePy, '8000' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
}
Check '假 LLM 服务可用（127.0.0.1:8000）' $true ''

# ---------- 1.5 准备密钥与模型配置（跑完恢复） ----------
#
# 两个坑，都不是产品缺陷，是测试环境缺件：
#   1. ChatClient 遇到空 API Key 直接 `Fail("还没有填写这个供应商的 API Key")`
#      —— 假服务不校验 Key，但客户端要求非空。SecretsFile 是 DPAPI 加密的
#      （CurrentUser + 固定熵），所以这里用同样的熵加密一个假 Key 写进去。
#   2. models.json 得指向假服务的端口（8000）。
# 两者都要**先备份、后恢复**，不能动用户真实配置。
Add-Type -AssemblyName System.Security
$secretsPath = Join-Path $dataRoot 'secrets.dat'
$modelsPath = Join-Path $dataRoot 'models.json'
$bakSuffix = '.bak-uicheck-' + (Get-Date -Format 'yyyyMMddHHmmss')
$secretsBak = if (Test-Path $secretsPath) { $secretsPath + $bakSuffix } else { $null }
$modelsBak = if (Test-Path $modelsPath) { $modelsPath + $bakSuffix } else { $null }
if ($secretsBak) { Copy-Item $secretsPath $secretsBak -Force }
if ($modelsBak) { Copy-Item $modelsPath $modelsBak -Force }

$entropy = [System.Text.Encoding]::UTF8.GetBytes('SpiritScout.Desktop.v1')
$plain = [System.Text.Encoding]::UTF8.GetBytes('{"local":"dummy-key-for-check"}')
$blob = [System.Security.Cryptography.ProtectedData]::Protect(
    $plain, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
[System.IO.File]::WriteAllBytes($secretsPath, $blob)
Check '写入测试密钥（DPAPI）' $true ''

$modelsJson = @"
{
 "Providers": [
  { "Name": "local", "Kind": "openai", "BaseUrl": "http://127.0.0.1:8000/v1", "ApiKeyRef": "" }
 ],
 "Models": [
  { "Name": "local-model", "ProviderName": "local", "ModelId": "local-model",
    "ContextWindow": 32768, "Enabled": true, "PricePerMillionCny": 0 }
 ]
}
"@
[System.IO.File]::WriteAllText($modelsPath, $modelsJson, (New-Object System.Text.UTF8Encoding($false)))
Check '写入测试模型配置' (Test-Path $modelsPath) 'local → 127.0.0.1:8000'

# ---------- 2. 造一个干净的会话 ----------
$now = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
$sess = [ordered]@{
    Session = [ordered]@{
        SchemaVersion = 1
        Id = $sessId
        Target = 'uicheck.local'
        Mode = 1
        Phase = 0
        Mission = '会话链路自检'
        FlagFormat = ''
        Title = '会话自检'
        Tokens = 0
        MessageCount = 0
        CreatedAt = $now
        UpdatedAt = $now
    }
    Messages = @()
}
$sess | ConvertTo-Json -Depth 6 | Set-Content -Path $sessFile -Encoding UTF8
Check '准备测试会话' (Test-Path $sessFile) $sessId

# ---------- 3. 起 exe 并发一条 ----------
Get-Process -Name '灵枢' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $exe } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "--page session:$sessId --send-text `"$sendText`" --no-focus"
$psi.UseShellExecute = $false
$app = [System.Diagnostics.Process]::Start($psi)
$started = $true
Check '真机启动并进入会话' $started ("pid=" + $app.Id)

# ---------- 4. 轮询会话文件 ----------
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$msgs = @()
$phase = -1
$answered = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800
    if (-not (Test-Path $sessFile)) { continue }
    $sf = $null
    try { $sf = Get-Content $sessFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
    if ($null -eq $sf) { continue }
    $msgs = @($sf.Messages)
    $phase = [int]$sf.Session.Phase
    $hasUser = $false
    $hasAsst = $false
    foreach ($m in $msgs) {
        if ($m.Role -eq 'user' -and $m.Content -like "*$sendText*") { $hasUser = $true }
        if ($m.Role -eq 'assistant' -and $m.Content -like "*$expectReply*") { $hasAsst = $true }
    }
    if ($hasUser -and $hasAsst) { $answered = $true; break }
    # 已经失败/停止了就别再等
    if ($phase -eq 4 -or $phase -eq 5) { break }
}

# ---------- 5. 判定 ----------
$userMsg = @($msgs | Where-Object { $_.Role -eq 'user' })
$asstMsg = @($msgs | Where-Object { $_.Role -eq 'assistant' })
$toolMsg = @($msgs | Where-Object { $_.Role -eq 'tool' })

Check '用户消息落到会话' ($userMsg.Count -ge 1 -and ($userMsg[0].Content -like "*$sendText*")) ("共 " + $msgs.Count + " 条消息")
Check '助手回复落到会话' ($asstMsg.Count -ge 1) $(if ($asstMsg.Count -ge 1) { "回复首段：「" + ($asstMsg[0].Content -replace "`r?`n", ' ').Substring(0, [Math]::Min(28, ($asstMsg[0].Content -replace "`r?`n", ' ').Length)) + "…」" } else { "没有 assistant 消息" })
Check '回复内容来自假模型' ($asstMsg.Count -ge 1 -and $asstMsg[0].Content -like "*$expectReply*") $expectReply
Check '阶段推进到 Completed' ($phase -eq 3) ("Phase=" + $phase + "（3=Completed）")
if ($toolMsg.Count -gt 0) { $lines.Add("  [info] 本轮触发了 " + $toolMsg.Count + " 次工具调用") }

# ---------- 6. 收尾 ----------
if ($null -ne $app) { try { $app.Kill() } catch { } }
if ($null -ne $svcProc) { try { $svcProc.Kill() } catch { } }

# 还原用户真实配置：有备份就放回去；原本不存在（我们新建的）就删掉。
# 不还原会留下一个假 API Key —— 用户以为配好了，实际调用会失败。
Start-Sleep -Milliseconds 400
try {
    if ($secretsBak -and (Test-Path $secretsBak)) {
        Move-Item $secretsBak $secretsPath -Force
    } elseif (Test-Path $secretsPath) {
        Remove-Item $secretsPath -Force
    }
} catch { $lines.Add("  [warn] secrets 还原失败：" + $_.Exception.Message) }
try {
    if ($modelsBak -and (Test-Path $modelsBak)) {
        Move-Item $modelsBak $modelsPath -Force
    }
} catch { $lines.Add("  [warn] models 还原失败：" + $_.Exception.Message) }

# 清掉测试会话：结果已经写进报告了，留着会挂在用户侧栏里
# （每次跑都重建，不需要历史）。
try {
    if (Test-Path $sessFile) { Remove-Item $sessFile -Force }
    $idxFile = Join-Path $sessDir "_index.json"
    if (Test-Path $idxFile) { Remove-Item $idxFile -Force }
} catch { }

$lines.Add("")
$lines.Add("失败项: " + $issues.Count)
[System.IO.File]::WriteAllLines($OutFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
$lines | ForEach-Object { Write-Host $_ }

if ($issues.Count -gt 0) {
    Write-Host ""
    Write-Host ("会话端到端：{0} 项失败（报告 {1}）" -f $issues.Count, $OutFile) -ForegroundColor Red
    foreach ($i in $issues) { Write-Host ("  - " + $i) -ForegroundColor Red }
    exit 1
}
Write-Host ""
Write-Host "会话端到端全部通过（报告 $OutFile）" -ForegroundColor Green
exit 0
