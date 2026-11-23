# 灵枢桌面版 —— 编译脚本
#
# 用 Windows 自带的 csc.exe 编译，不需要装 .NET SDK。
# 产物是一个独立的 exe：.NET Framework 4.8 是 Windows 10/11 自带的运行时，
# 所以用户那边双击就能跑，不用装任何东西。
#
# 用法：
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'DesktopApp\build\build.ps1')))"
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'DesktopApp\build\build.ps1')))"   # 原参数：-Run

[CmdletBinding()]
param(
    [string]$OutFile = '',
    [string]$Icon = '',
    [switch]$Run,
    [switch]$NoOptimize
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$t) { Write-Host "[>] $t" -ForegroundColor Cyan }
function Write-Ok([string]$t)   { Write-Host "[+] $t" -ForegroundColor Green }
function Write-Err([string]$t)  { Write-Host "[!] $t" -ForegroundColor Red }

$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot  = Split-Path -Parent $buildDir
$srcDir   = Join-Path $appRoot 'src'

if (-not $OutFile) {
    $outDir = Join-Path $appRoot 'out'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $OutFile = Join-Path $outDir '灵枢.exe'
}

# 版本号：**唯一来源是 src\Core\AppInfo.cs 的 `const string Version`**。
#
# 原来这里找的是 `src\Version.cs`，而那个文件从来没建过 ——
# 于是每次都静默走 `$version = '2.0.0'` 的兜底。
# **文件改名/搬位置时构建照样成功，但打出去的版本号是旧的**，很难发现。
#
# 现在：找不到文件或解析不出，直接抛错（宁可构建失败，不要发出错误的版本号）。
$versionFile = Join-Path $srcDir 'Core\AppInfo.cs'
if (-not (Test-Path $versionFile)) {
    Write-Err "找不到版本号来源：$versionFile"
    exit 1
}
# 正则必须锚到 `public const`：只写 `const string Version = "..."` 的话，
# 注释里的示例文字也会被匹配到（踩过：版本号被打成了注释里的示例值）。
$vm = [regex]::Match((Get-Content $versionFile -Raw -Encoding UTF8), 'public const string Version\s*=\s*"([^"]+)"')
if (-not $vm.Success) {
    Write-Err "AppInfo.cs 里找不到 const string Version = \"...\""
    exit 1
}
$version = $vm.Groups[1].Value
Write-Step "版本 $version（来自 Core\AppInfo.cs）"

# 找 csc.exe
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    Write-Err '找不到 csc.exe（需要 .NET Framework 4.x）'
    exit 1
}

# 源文件
$sources = Get-ChildItem -Path $srcDir -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { Write-Err "src 下没有 .cs"; exit 1 }
Write-Step "源文件 $($sources.Count) 个"

# 图标。同一个 ico 要用两次，**缺一不可**：
#   · /win32icon  → 资源管理器里 exe 文件的图标
#   · /resource   → 运行时 `Theme.AppIcon()` 取清单资源 "icon.ico"，
#                   标题栏/任务栏/托盘气泡用它。只做前者的话窗口图标是
#                   WinForms 默认的通用图标（用户看到的就是「图标什么都没有」）。
if (-not $Icon) {
    $candidate = Join-Path $appRoot 'assets\icon.ico'
    if (Test-Path $candidate) { $Icon = $candidate }
}
$iconRes = ''
if ($Icon) { $iconRes = $Icon + ',icon.ico' }

# 技能库随 exe 一起发（嵌入资源）。文件由 build\export_skills.py 生成。
$skillsJson = Join-Path $srcDir 'Resources\skills.json'
if (-not (Test-Path $skillsJson)) {
    Write-Err "缺少 $skillsJson —— 先运行 build\export_skills.py"
    exit 1
}
$skillsRes = $skillsJson + ',skills.json'

# 高 DPI 感知声明（不声明的话高分屏上界面会被位图拉伸）
$manifestPath = Join-Path $buildDir 'app.manifest'
if (-not (Test-Path $manifestPath)) {
    Write-Err "缺少 $manifestPath"
    exit 1
}
Write-Step '高 DPI 感知：PerMonitorV2'
Write-Step ("技能库 " + [math]::Round((Get-Item $skillsJson).Length / 1KB) + " KB")

# **编译成败看 `csc` 的退出码，不看文件在不在。**
#
# 原来靠 `Test-Path $OutFile` 判断 —— 但**旧的 exe 一直都在**，
# 所以编译失败时它照样为真，脚本会打印「[+] 产物：…（1876 KB）」并 exit 0。
#
# （249 轮实测：故意写一个语法错误，csc 报了 CS1513，而 build.ps1 仍然
#   打印产物、退出码 0、exe 大小和时间都没变 —— 完全静默。）
#
# 也试过「先编到 `灵枢.new.exe` 再 Move 覆盖」，但**这个环境不允许**：
# csc 报 `CS0016 未能写入输出文件 … 拒绝访问`（新建 exe 被杀软拦，覆盖旧的却可以）。
# 换成 `灵枢.exe.new` 也不行 —— csc 校验 /out 的扩展名。
#
# 所以还是直接输出到 $OutFile。**csc 编译失败时根本不碰旧文件**
# （实测：语法错误后 exe 大小和 mtime 都没变），旧版本天然保住。
$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    ('/out:' + $OutFile),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    '/r:System.Net.Http.dll',
    '/r:System.Web.Extensions.dll',
    '/r:System.Security.dll',
    ('/win32icon:' + $(if ($Icon) { $Icon } else { '' })),
    ('/resource:' + $iconRes),
    ('/resource:' + $skillsRes),
    ('/win32manifest:' + $manifestPath)
)
if ($NoOptimize) { $cscArgs = $cscArgs | Where-Object { $_ -ne '/optimize+' } }
if (-not $Icon) { $cscArgs = $cscArgs | Where-Object { $_ -notlike '/win32icon:*' -and $_ -ne '/resource:,' } }

# 版本资源
$verParts = $version.Split('.')
while ($verParts.Count -lt 4) { $verParts += '0' }
$verInfo = @"
using System.Reflection;
[assembly: AssemblyTitle("灵枢 SpiritScout")]
[assembly: AssemblyProduct("SpiritScout")]
[assembly: AssemblyVersion("$($verParts[0]).$($verParts[1]).$($verParts[2]).$($verParts[3])")]
[assembly: AssemblyFileVersion("$($verParts[0]).$($verParts[1]).$($verParts[2]).$($verParts[3])")]
[assembly: AssemblyCompany("SpiritScout")]
[assembly: AssemblyDescription("灵枢桌面版")]
"@
$verTmp = Join-Path $buildDir '_version_tmp.cs'
Set-Content -Path $verTmp -Value $verInfo -Encoding UTF8

Write-Step '编译中…'
# **不要写成 `& $csc … | ForEach-Object { … }`** —— 那样 $LASTEXITCODE 的取值
# 依赖管道最后一环，不可靠。先拿到原始输出和退出码，再逐行打印。
# **遇到「拒绝访问」要重试。**
#
# 这个环境里杀软会短暂锁住刚写过的 exe —— 实测 `csc` 会报
# `CS0016 未能写入输出文件 … 拒绝访问`，等几秒再跑就好了。
# 没有重试的话，一次偶发的锁会让构建莫名其妙地失败（而且报错指向 exe 而不是
# 「文件被占用」，很容易误判成代码问题）。
#
# 只对 CS0016 重试：那明确是「写不进去」，不是语法错误。
$cscExit = 1
$cscOut = @()
for ($try = 1; $try -le 5; $try++) {
    $cscOut = & $csc @cscArgs $verTmp $sources 2>&1
    $cscExit = $LASTEXITCODE
    if ($cscExit -eq 0) { break }
    $isLock = ($cscOut | Where-Object { $_.ToString() -match 'CS0016' }).Count -gt 0
    if (-not $isLock) { break }
    if ($try -lt 5) {
        Write-Host ("  csc 报 CS0016（目标文件被占用），3 秒后重试（第 $try/5 次）…") -ForegroundColor Yellow
        Start-Sleep -Seconds 3
    }
}
$cscOut | ForEach-Object {
    $line = $_.ToString()
    if ($line -match 'error') { Write-Host "  $line" -ForegroundColor Red }
    elseif ($line -match 'warning') { Write-Host "  $line" -ForegroundColor Yellow }
    else { Write-Host "  $line" }
}
Remove-Item $verTmp -Force -ErrorAction SilentlyContinue

if ($cscExit -ne 0) {
    if (Test-Path $OutFile) {
        $oldSize = [math]::Round((Get-Item $OutFile).Length / 1KB)
        Write-Err "编译失败（csc 退出码 $cscExit）。**旧的 exe 保留着**：$OutFile（$oldSize KB）"
    } else {
        Write-Err "编译失败（csc 退出码 $cscExit），而且没有旧的 exe 可用"
    }
    exit 1
}

$size = [math]::Round((Get-Item $OutFile).Length / 1KB)
Write-Ok "产物：$OutFile（$size KB）"

if ($Run) {
    Write-Step '启动…'
    Start-Process $OutFile
}

# **显式 exit 0。**
# 不加的话，脚本退出码会受 `$LASTEXITCODE` 残留影响 ——
# 259 轮实测：csc 明明成功了，从 `subprocess` 拿到却是 exit=1。
# 显式写出来，调用方（verify_all、CI、bat 脚本）才拿得准。
exit 0
