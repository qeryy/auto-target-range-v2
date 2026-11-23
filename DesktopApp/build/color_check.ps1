# 配色一致性检查
#
# ## 为什么需要这个
#
# 前面十几轮一直在改配色：SaltUI 的调色板、气泡的 color-mix、状态胶囊的 soft 色、
# 卡片阴影的 alpha……每次都靠「看着像对的」判断。
#
# 但**颜色是可以算出来的**。web 版的 CSS 写的是：
#
#   .tl-user .bubble { background: color-mix(in srgb, var(--accent) 11%, var(--surface)); }
#
# 意思是「accent 占 11%，surface 占 89%，逐通道线性混合」。
# 桌面版的 `Theme.Mix` 就是这个公式。所以理论值是确定的：
#
#   Mix(#0088FF, #202020, 0.11) = #1C2B39
#
# 这个脚本把截图里的实际像素数和理论值对一遍。**不通过就说明
# 「算出来的颜色」和「画出来的颜色」中间有一环断了**——可能是 Mix 写错、
# 可能是某个分支没走到、可能是截图拍到的是旧版本。
#
# ## 用法
#
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\color_check.ps1')))"
#   powershell -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 'build\color_check.ps1')))"   # 原参数：-Shot docs\screenshots\session.png
#
# 需要先有一张**同时包含用户气泡和助手气泡**的会话截图。
# 默认看 `docs\screenshots\session.png`——那张是 `发布说明.md` 里用的，
# 内容装得下（不会被滚走）。
#
# ## 为什么不放进 check_all
#
# check_all 的 8 项都是「结构/布局/行为」检查，跑一遍 167 秒。
# 这项是「像素级配色回归」，只在改配色的那几轮有用，而且依赖一张截图。
# 单独跑（2 秒）更合适。

[CmdletBinding()]
param(
    [string]$Shot = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$buildDir = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { Join-Path (Get-Location).Path 'build' }
$appRoot = Split-Path -Parent $buildDir
if (-not $Shot) { $Shot = Join-Path $appRoot 'docs\screenshots\session.png' }
if (-not $OutFile) { $OutFile = Join-Path $appRoot 'out\reports\color-report.txt' }
$reportDir = Split-Path -Parent $OutFile
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

if (-not (Test-Path $Shot)) {
    Write-Host "找不到截图：$Shot" -ForegroundColor Red
    Write-Host "先生成一张：跑 build\smoke.ps1 或手工截 docs\screenshots\session.png" -ForegroundColor Yellow
    exit 2
}

Add-Type -AssemblyName System.Drawing

# ---- 期望的颜色（与 Theme.cs 一一对应，改配色时两边一起改）----
#
# 2026-11-19：默认截图用 session.png。
#   首页背景铺了氛围光 + 噪点，精确 Bg 像素被有意盖掉，不适合做 Bg 回归；
#   会话页是平铺 Bg，且同时有用户/助手气泡和「灵」徽章，
#   6 个主题色 + 3 个混色能一次验全。首页图仍可用 -Shot 传入看分布。
$spec = @(
    # Min = 0 的项表示「这张截图上可能没有」，看到才验。
    # Bg 的 Tol 放到 5：带氛围噪点的图上每像素 +0~4 白，
    # 精确等于 #111113 的像素极少；±5 才能圈住「噪点下的主背景」。
    @{ Name = '主背景 Bg';            R = 0x11; G = 0x11; B = 0x13; Min = 80000;  Tol = 5 },
    @{ Name = '卡片 Surface';          R = 0x20; G = 0x20; B = 0x20; Min = 10000;  Tol = 2 },
    @{ Name = '次级面 SurfaceAlt';     R = 0x28; G = 0x28; B = 0x2A; Min = 0;      Tol = 2 },
    @{ Name = '正文 Text';             R = 0xEB; G = 0xEE; B = 0xF1; Min = 2000;   Tol = 6 },
    @{ Name = '强调 Accent';           R = 0x00; G = 0x88; B = 0xFF; Min = 30;     Tol = 12 },
    @{ Name = '紫 Violet';             R = 0xA3; G = 0x71; B = 0xF7; Min = 0;      Tol = 12 }
)

# ---- 混色理论值：Mix(a, b, wa) ----
function Mix([int]$ar, [int]$ag, [int]$ab, [int]$br, [int]$bg, [int]$bb, [double]$wa) {
    $wb = 1 - $wa
    return @{
        R = [int][Math]::Round($ar * $wa + $br * $wb)
        G = [int][Math]::Round($ag * $wa + $bg * $wb)
        B = [int][Math]::Round($ab * $wa + $bb * $wb)
    }
}

$accent = @{ R = 0x00; G = 0x88; B = 0xFF }
$surface = @{ R = 0x20; G = 0x20; B = 0x20 }
$violet = @{ R = 0xA3; G = 0x71; B = 0xF7 }
$line = @{ R = 0x44; G = 0x4D; B = 0x58 }

$mixSpec = @(
    # 默认截图是会话页：两种气泡和徽章都在，Min 给真值下限。
    # 换首页等没有气泡的图时用 -Shot 传入，这三项会因 Min 报 WARN ——
    # 那是「这张图验不了气泡」，不是配色错。
    @{ Name = '用户气泡底 (accent 11% + surface)';
       Want = (Mix $accent.R $accent.G $accent.B $surface.R $surface.G $surface.B 0.11); Min = 2000; Tol = 3 },
    @{ Name = '助手气泡底 (violet 9% + surface)';
       Want = (Mix $violet.R $violet.G $violet.B $surface.R $surface.G $surface.B 0.09); Min = 2000; Tol = 3 },
    @{ Name = '徽章描边 (violet 62% + line)';
       Want = (Mix $violet.R $violet.G $violet.B $line.R $line.G $line.B 0.62); Min = 50; Tol = 8 }
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("灵枢桌面版 · 配色一致性检查")
$lines.Add("时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add("截图：" + $Shot)
$lines.Add("")

$bmp = [System.Drawing.Bitmap]::FromFile($Shot)
try {
    $w = $bmp.Width; $h = $bmp.Height
    $lines.Add("尺寸：${w}x${h}")

    # ---- 一次扫描统计所有颜色（用字典，逐像素 GetPixel 太慢）----
    $counts = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $bytes = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        for ($y = 0; $y -lt $h; $y++) {
            $row = $y * $stride
            for ($x = 0; $x -lt $w; $x++) {
                $i = $row + $x * 4
                # BGRA → RGB 打包成 int。
                #
                # **必须先 [int] 转换再移位**：PowerShell 5.1 里 [byte] 类型的
                # `-shl` 会把结果截断回 byte，`0x24 -shl 8` 直接变 0。
                # 之前没转，所有颜色的高两位都丢了，`#202020` 被读成 `#000020`,
                # 于是 9 项全报「对不上」。
                $bb = [int]$bytes[$i]
                $gg = [int]$bytes[$i + 1]
                $rr = [int]$bytes[$i + 2]
                $key = ($rr -shl 16) -bor ($gg -shl 8) -bor $bb
                if ($counts.ContainsKey($key)) { $counts[$key] = $counts[$key] + 1 }
                else { $counts[$key] = 1 }
            }
        }
    } finally {
        $bmp.UnlockBits($data)
    }

    $total = $w * $h
    $lines.Add("唯一色：" + $counts.Count)
    $lines.Add("")

    $bad = 0
    function Near([int]$r, [int]$g, [int]$b, [int]$tr, [int]$tg, [int]$tb, [int]$tol) {
        return ([Math]::Abs($r - $tr) -le $tol) -and ([Math]::Abs($g - $tg) -le $tol) -and ([Math]::Abs($b - $tb) -le $tol)
    }

    $lines.Add("---- 主题色 ----")
    foreach ($s in $spec) {
        $n = 0
        foreach ($kv in $counts.GetEnumerator()) {
            $r = ($kv.Key -shr 16) -band 0xFF
            $g = ($kv.Key -shr 8) -band 0xFF
            $b = $kv.Key -band 0xFF
            if (Near $r $g $b $s.R $s.G $s.B $s.Tol) { $n += $kv.Value }
        }
        $hex = ('#{0:X2}{1:X2}{2:X2}' -f $s.R, $s.G, $s.B)
        if ($n -ge $s.Min) {
            $lines.Add(('  [ OK ] {0,-22} {1}  {2,8} px' -f $s.Name, $hex, $n))
        } else {
            $lines.Add(('  [WARN] {0,-22} {1}  {2,8} px  (期望 >= {3})' -f $s.Name, $hex, $n, $s.Min))
            $bad++
        }
    }

    $lines.Add("")
    $lines.Add("---- 混色（color-mix 等价）----")
    foreach ($s in $mixSpec) {
        $n = 0
        foreach ($kv in $counts.GetEnumerator()) {
            $r = ($kv.Key -shr 16) -band 0xFF
            $g = ($kv.Key -shr 8) -band 0xFF
            $b = $kv.Key -band 0xFF
            if (Near $r $g $b $s.Want.R $s.Want.G $s.Want.B $s.Tol) { $n += $kv.Value }
        }
        $hex = ('#{0:X2}{1:X2}{2:X2}' -f $s.Want.R, $s.Want.G, $s.Want.B)
        if ($n -ge $s.Min) {
            $lines.Add(('  [ OK ] {0,-38} {1}  {2,8} px' -f $s.Name, $hex, $n))
        } else {
            $lines.Add(('  [WARN] {0,-38} {1}  {2,8} px  (期望 >= {3})' -f $s.Name, $hex, $n, $s.Min))
            $bad++
        }
    }

    $lines.Add("")
    $lines.Add("---- 出现最多的 8 种颜色 ----")
    $sorted = $counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8
    foreach ($kv in $sorted) {
        $r = ($kv.Key -shr 16) -band 0xFF
        $g = ($kv.Key -shr 8) -band 0xFF
        $b = $kv.Key -band 0xFF
        $pct = [Math]::Round($kv.Value * 100.0 / $total, 2)
        $lines.Add(('  #{0:X2}{1:X2}{2:X2}  {3,6}%' -f $r, $g, $b, $pct))
    }

} finally {
    $bmp.Dispose()
}

$lines.Add("")
$lines.Add(("=" * 60))
if ($bad -eq 0) { $lines.Add("配色与 Theme.cs / web CSS 一致") }
else { $lines.Add("有 $bad 项颜色对不上") }

$lines | ForEach-Object { Write-Host $_ }
$lines -join "`r`n" | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ""
Write-Host "报告: $OutFile" -ForegroundColor Cyan
if ($bad -ne 0) { exit 1 }
exit 0
