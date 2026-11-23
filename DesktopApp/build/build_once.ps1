# build/build_once.ps1 —— 一次性编译测试用 exe（e2e + fit）
#
# 用法：源码改动后运行一次
#     powershell -NoProfile -File build\build_once.ps1
#
# 与 *_check.ps1 的分工（2026-11-22 定）：
#   build_once.ps1  —— 只负责编译：csc → out\build-XXX\X.exe
#   *_check.ps1     —— 只负责运行：直接用上面的产物跑断言，完全不碰 csc
#
# 为什么这么拆：
#   「powershell 拉 csc → 临时目录产 exe → 立刻跑」这条链被火绒启发式
#   误判为 TrojanSpy/MSIL.Stealer.co（21 次）。把「编译」和「运行」分开，
#   check 脚本永远不再拉 csc，就不构成那条链。
#
# 产物固定到 build\out\build-*：
#   · 便于白名单管理（只需对 build\ 一条规则）
#   · 不再碰 %TEMP% 随机目录（那是火绒判定的关键特征）

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
Set-Location $appRoot

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Host '[FAIL] csc.exe not found'; exit 1 }

$skills = Join-Path $appRoot 'src\Resources\skills.json'
$sources = Get-ChildItem 'src' -Recurse -Filter '*.cs' |
    Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName }

function Invoke-Csc([string]$Exe, [string[]]$ExtraArgs, [string[]]$Src) {
    # csc「新建」exe 在这个环境下会被拒，先写 0 字节占位改成「覆盖」（老 workaround）
    if (-not (Test-Path $Exe)) { try { [IO.File]::WriteAllBytes($Exe, @()) } catch { } }
    $all = $ExtraArgs + $Src
    $out = & $csc @all 2>&1
    $ok = (Test-Path $Exe) -and ((Get-Item $Exe).Length -gt 1024)
    if (-not $ok) {
        Start-Sleep -Seconds 2
        $out = & $csc @all 2>&1
        $ok = (Test-Path $Exe) -and ((Get-Item $Exe).Length -gt 1024)
    }
    return @{ ok = $ok; log = $out }
}

function Build-One {
    param(
        [string]$TestCs, [string]$OutSub, [string]$ExeName,
        [string]$MainName, [string]$ExtraArg = ''
    )
    $src = Join-Path $buildDir ('tests\' + $TestCs)
    if (-not (Test-Path $src)) { Write-Host "[FAIL] missing: $src"; return $false }
    $outDir = Join-Path $buildDir ('out\' + $OutSub)
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $exe = Join-Path $outDir $ExeName
    $all = @($src) + $sources

    Write-Host ('编译 {0} + {1} 个源文件 -> {2}' -f $TestCs, ($all.Count-1), $exe)
    $common = @(
        '/nologo', '/target:exe', "/main:$MainName", "/out:$exe",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        '/r:System.Security.dll', '/r:System.Net.Http.dll',
        "/resource:$skills,skills.json"
    )
    if ($ExtraArg) { $common += $ExtraArg }
    $r = Invoke-Csc $exe $common $all
    if (-not $r.ok) {
        Write-Host '[FAIL] 编译失败：'
        $r.log | Select-Object -First 8 | ForEach-Object { Write-Host ('  ' + $_) }
        return $false
    }
    Write-Host ('[OK] built {0} ({1:N1} KB)' -f (Get-Item $exe).Name, ((Get-Item $exe).Length/1KB))
    return $true
}

$manifest = Join-Path $buildDir 'app.manifest'
$ok1 = Build-One -TestCs 'AgentLoopE2E.cs' -OutSub 'build-e2e' -ExeName 'e2e.exe' -MainName 'AgentLoopE2E'
$ok2 = Build-One -TestCs 'LayoutFit.cs'    -OutSub 'build-fit' -ExeName 'fit.exe' -MainName 'LayoutFit' -ExtraArg "/win32manifest:$manifest"

if (-not ($ok1 -and $ok2)) { exit 1 }
exit 0
