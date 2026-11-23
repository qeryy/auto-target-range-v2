# build/shots.ps1 —— 逐页真窗口截图
#
# 用 exe 自己的 `--page <id> --screenshot <png> --no-focus` 拍：
# 页面画在**真实主窗体**里（侧栏、顶栏、氛围底都是真的），看到的就是用户看到的。
# fit_check 的离屏宿主拍页面只会得到空白，所以页面像素只能这么来。
#
# 用法（本机不能用 -File，见 AGENTS/踩坑清单）：
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\shots.ps1')))"
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\shots.ps1'))) -Out 'docs\screenshots'"
#
# 字号 / DPI / 配色改完之后，重刷 docs\screenshots 就带 -Out 指过去。
# 截图模式不落盘窗口状态（Program.cs 里判过 ShotPath），所以不会污染用户的位置记忆。

param([string]$Out = 'out\shots')

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
Set-Location $appRoot

$exe = Join-Path $appRoot 'out\灵枢.exe'
if (-not (Test-Path $exe)) { Write-Host "  [FAIL] missing exe: $exe"; exit 1 }

$outDir = $Out
if (-not [System.IO.Path]::IsPathRooted($outDir)) { $outDir = Join-Path $appRoot $outDir }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$pages = @('home', 'dash', 'usage', 'agents', 'models', 'projects',
           'exp', 'skills', 'mcp', 'localtools', 'notify', 'settings', 'about')

# 路径用正斜杠：反斜杠在「脚本块 + 变量插值」下容易把整段路径吃掉（踩过）
$outForward = ($outDir -replace '\\', '/')
$bad = 0
foreach ($p in $pages) {
    $png = "$outForward/$p.png"
    Remove-Item $png -Force -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath $exe -PassThru -Wait -ArgumentList @(
        '--no-focus', '--page', $p, '--screenshot', $png)
    if (-not (Test-Path $png)) {
        Write-Host "  [FAIL] 截图没产出：$p (exit $($proc.ExitCode))"
        $bad++; continue
    }
    $kb = [int]((Get-Item $png).Length / 1024)
    # 空白图压出来只有几 KB：低于阈值按失败算，别把「拍了一张空的」当成通过
    if ($kb -lt 30) { Write-Host "  [FAIL] 图太小（多半是空白）：$p ${kb}KB"; $bad++; continue }
    Write-Host "  [OK ] $p  ${kb}KB"
}
if ($bad -gt 0) { Write-Host "  [FAIL] $bad 页没截出来"; exit 1 }
Write-Host "  全通过 $($pages.Count) 页截图 -> $outDir"
exit 0
