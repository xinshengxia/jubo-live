
# Build-Portable.ps1 —— 把开发版（加固源码）同步进绿色封装包，并同步 mpv 脚本
#   pwsh -File .\Build-Portable.ps1                                  # 默认包：<输出目录>
#   pwsh -File .\Build-Portable.ps1 -Package '.\artifacts\portable'   # 加固版副本
#
# 两种包布局都支持：
#   · 不带 src\ 的包（原封装）：直接用 live-hub 的源码编出 <包>\看直播.exe；
#   · 带 src\ 的包（加固版副本）：先把源码/测试同步进 <包>\src、<包>\tests，
#     再跑包内自己的 <包>\src\build.ps1（它负责编译 + 离线测试 + 自检）。
# 绿色包与开发版是同一份源码：exe 启动时看自己旁边有没有 bin\mpv\mpv.exe 决定便携/开发布局。
param(
    [string]$Package = '',
    # 凭据处理策略（2026-09-27 改，评估报告 §3）：
    #   **默认剥离**包里的登录 Cookie 与浏览器 profile —— 打包脚本的默认行为应该安全侧，
    #   原来反过来（默认保留、只警告），只要忘了加 -StripCredentials，发出去的包就带着
    #   完整账号登录态。自用便携包要保留登录态（换机不重扫码）时显式加 -IncludeCredentials。
    [switch]$IncludeCredentials
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Package) { $Package = Join-Path (Split-Path -Parent $root) '看直播（封装）' }

# ---- 工作区是否干净：影响打进包里的版本号 ----------------------------------
# 版本号是 `headcommit[-dirty]`。在脏工作区上打包，包里就会带 `-dirty` 后缀 ——
# 那**不是错误**，它如实说明了「这份代码含有未提交的改动」。但如果你以为
# 「包 = 某个 commit」，就会拿着一个对不上的标签去排查问题。
# 2026-09-25 实际踩过：提交前构建、提交后忘了重建，包里带着 -dirty。
# 这里只**警告不阻断** —— 中途构建绿色包是合理需求（比如想先试试效果）。
$gitState = 'nogit'
$dirtyFiles = @()
try {
    Push-Location $root
    $head = (git rev-parse --short HEAD 2>$null | Select-Object -First 1)
    if ($head) {
        $gitState = $head.Trim()
        $dirtyFiles = @(git status --porcelain 2>$null)
        if ($dirtyFiles.Count -gt 0) { $gitState += '-dirty' }
    }
} catch { } finally { Pop-Location }

if ($dirtyFiles.Count -gt 0) {
    Write-Output ''
    Write-Output '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!'
    Write-Output ('!! 工作区不干净：有 ' + $dirtyFiles.Count + ' 个文件未提交')
    Write-Output '!!'
    Write-Output '!! 打进包里的版本号会带 -dirty 后缀，比如 0.0.0+abc1234-dirty。'
    Write-Output '!! 那不是 bug —— 它如实说明这份代码含未提交改动 —— 但「包 = 某个'
    Write-Output '!! commit」这个前提就不成立了，事后拿版本号排查会对不上。'
    Write-Output '!!'
    Write-Output '!! 正确顺序是【先提交，再打包】。如果你就是想中途试构建，忽略即可。'
    Write-Output '!!'
    $show = if ($dirtyFiles.Count -gt 8) { $dirtyFiles[0..7] } else { $dirtyFiles }
    foreach ($line in $show) { Write-Output ('!!   ' + $line) }
    if ($dirtyFiles.Count -gt 8) { Write-Output ('!!   …还有 ' + ($dirtyFiles.Count - 8) + ' 个') }
    Write-Output '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!'
    Write-Output ''
} else {
    Write-Output ('工作区干净，版本号将标记为 ' + $gitState)
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '找不到 .NET Framework C# 编译器。' }
if (-not (Test-Path -LiteralPath $Package -PathType Container)) { throw "封装包目录不存在：$Package" }
if (-not (Test-Path -LiteralPath (Join-Path $Package 'bin\mpv\mpv.exe') -PathType Leaf)) { throw "这个目录不像封装包（没有 bin\mpv\mpv.exe）：$Package" }

$exe = Join-Path $Package '看直播.exe'
# 2026-09-25 起单文件约束解除：启动器 + src\ 下各分片一起编译，没有生成器了。
# ⚠️ 2026-09-28 改成**动态扫 src\ 下的子目录**，不写死 Danmaku/App ——
#    写死过一次：加 `src\App\` 时漏改包内那份 `src\build.ps1`，整包构建不出来。
$source = @((Join-Path $root 'live-hub-v2.cs'))
Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory | Sort-Object Name | ForEach-Object {
    $source += @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
}
$packageSource = Join-Path $Package 'src\live-hub-v2.cs'
$packageBuild = Join-Path $Package 'src\build.ps1'
$hasSource = Test-Path -LiteralPath $packageSource -PathType Leaf

# 1) 备份将被覆盖的文件
$backup = Join-Path $root ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-portable-sync')
New-Item -ItemType Directory -Force -Path $backup | Out-Null
# 注意：'使用说明.md' 只进**备份**清单，本脚本**不会**覆盖它 —— 两个绿色包各有一份「包内视角」的
# 精简手册（讲便携布局 / 怎么拷走 / 常见问题），由人工维护；这里只备份，改它请直接改包内那份。
$targets = @('看直播.exe', '看直播.exe.config', '使用说明.md', 'live-title.lua', 'lua\live-title.lua', 'bin\mpv\portable_config\scripts\live-title.lua', 'emoji-sheet.png', 'emoji-sheet.txt')
if ($hasSource) { $targets += @('src\live-hub-v2.cs', 'tests\HardeningTests.cs', 'tests\README.md') }
foreach ($rel in $targets) {
    $src = Join-Path $Package $rel
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $backup ($rel -replace '\\', '_')) -Force }
}
Write-Output ("包目录： " + $Package)
Write-Output ("备份 -> " + $backup)
Copy-Item -LiteralPath (Join-Path $root 'app.config') -Destination ($exe + '.config') -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $Package -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $Package -Recurse -Force

# 2) 带 src\ 的包：先同步源码与测试
if ($hasSource) {
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $Package 'src') -Force
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $Package 'src') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'live-hub-v2.cs') -Destination $packageSource -Force
    # app.ico 也要跟过去：包内 src\build.ps1 会用 /win32icon:$srcRoot\app.ico
    foreach ($asset in @('app.config', 'app.manifest', 'app.ico')) {
        Copy-Item -LiteralPath (Join-Path $root $asset) -Destination (Join-Path $Package ('src\' + $asset)) -Force
    }
    # 弹幕分片目录整体同步（原来只有一个聚合文件，现在是 11 个源文件）
    $pkgDanmaku = Join-Path $Package 'src\Danmaku'
    New-Item -ItemType Directory -Force -Path $pkgDanmaku | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'src\Danmaku') -Filter '*.cs' -File |
        ForEach-Object { Copy-Item $_.FullName $pkgDanmaku -Force }
    # 2026-09-27 起还有 src\App\*.cs（partial 拆分出来的另一半：路径与配置解析）
    $pkgApp = Join-Path $Package 'src\App'
    New-Item -ItemType Directory -Force -Path $pkgApp | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'src\App') -Filter '*.cs' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName $pkgApp -Force }
    # 测试：HardeningTests.cs 与 DanmakuTests.cs 是同一个 partial class 的两半，必须都同步
    Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' -File |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $Package 'tests') -Force }
    Copy-Item -LiteralPath (Join-Path $root 'tests\README.md') -Destination (Join-Path $Package 'tests\README.md') -Force
    # 2.2) 包内自编译脚本：**从仓库模板复制**，不再人工维护包内那份。
    #
    # 为什么（2026-09-28）：`<包>\src\build.ps1` 原来只存在于各绿色包内部、不在 git 里，
    # 人工维护。往 `src\` 下加 `App\` 目录时，本脚本与 Build-Hardened.ps1 都改了，
    # 它没跟着改 → 包内编译报 `CS0426: 类型"LiveHub"中不存在类型名"AclOutcome"`，
    # **整包构建不出来**。现在由这里覆盖写，跟仓库走就不会再漂。
    # 模板里的源码列表是**动态扫 src\ 下每个子目录**的，以后加目录自动生效。
    Copy-Item -LiteralPath (Join-Path $root 'tools\portable-src-build.ps1') -Destination $packageBuild -Force
    $source = @((Join-Path $Package 'src\live-hub-v2.cs')) +
        @(Get-ChildItem -LiteralPath (Join-Path $Package 'src') -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName } })
    Write-Output ('同步 src\live-hub-v2.cs + src\ 下所有子目录（' + $source.Count + ' 个源文件）+ tests\*.cs + tests\README.md')
    Write-Output '包内 src\build.ps1 已按仓库模板覆盖（以后不会再漂）'
}

# 2.5) 彩色 emoji 精灵图（弹幕用；构建期由 tools/gen-emoji-sheet.ps1 生成）
#
# **必须放在第 3 步（包内编译 + 离线测试）之前**：离线测试里有一组专门校验
# 「精灵图清单 ↔ 运行时查表一致」的用例，清单陈旧/缺失就会失败 —— 这正是它该抓的。
# 之前放在最后，导致带 src\ 的包用**上一次**的旧清单跑测试，白失败一轮。
# 清单（emoji-sheet.txt）里写着格子尺寸与区段/序列表，**运行时读它**，所以它必须跟着图一起走。
$emojiSheet = Join-Path $root 'emoji-sheet.png'
$emojiManifest = Join-Path $root 'emoji-sheet.txt'
if ((Test-Path -LiteralPath $emojiSheet) -and (Test-Path -LiteralPath $emojiManifest)) {
    foreach ($dir in @($Package, (Join-Path $Package 'src'))) {
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        Copy-Item -LiteralPath $emojiSheet -Destination (Join-Path $dir 'emoji-sheet.png') -Force
        Copy-Item -LiteralPath $emojiManifest -Destination (Join-Path $dir 'emoji-sheet.txt') -Force
    }
    Write-Output '同步 emoji-sheet.png + emoji-sheet.txt（包根 + src\）'
} else {
    Write-Output '警告：找不到 emoji-sheet.png / emoji-sheet.txt，便携包里将没有彩色 emoji（会退回系统字体单色字形）'
}

# 2.6) 界面字体 HarmonyOS Sans SC（华为免费字体，随包携带 —— 免安装）
#
# 为什么必须跟着包走：程序启动时用 AddFontResourceEx(..., FR_PRIVATE) 从 exe 同级的
# fonts\ 目录注册进**本进程**的 GDI 字体表。目录不在就静默退回系统雅黑 ——
# 界面还能用，但用户看到的字体就不是设计的那套了。
#
# 许可（fonts\LICENSE.txt，HarmonyOS Sans Fonts License Agreement）允许
# royalty-free 地 embed / bundle / redistribute，条件是**随附版权声明与本协议** ——
# 所以 LICENSE.txt 必须一起复制，不能只拷 ttf。
$fontDir = Join-Path $root 'fonts'
if (Test-Path -LiteralPath $fontDir -PathType Container) {
    $fontTarget = Join-Path $Package 'fonts'
    New-Item -ItemType Directory -Force -Path $fontTarget | Out-Null
    # ⚠️ 不能用 `-LiteralPath (Join-Path $fontDir '*')` —— **-LiteralPath 不展开通配符**，
    #    会报「找不到路径 ...\fonts\*」。逐个文件复制最稳。
    Get-ChildItem -LiteralPath $fontDir -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $fontTarget $_.Name) -Force
    }
    $fontCount = (Get-ChildItem -LiteralPath $fontTarget -File | Measure-Object).Count
    $fontSize = [math]::Round(((Get-ChildItem -LiteralPath $fontTarget -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Output "同步 fonts\（HarmonyOS Sans SC + LICENSE.txt，$fontCount 个文件，$fontSize MB）"
} else {
    Write-Output '警告：找不到 fonts\ 目录，便携包里将退回系统雅黑（界面能用，但字体不是设计的那套）'
}

# 2.7) 测试样本：离线回归里有几条要用**真实**文件（WebP 表情样本）。
#
# 为什么必须跟着包走：包内 `src\build.ps1` 会跑同一套离线测试，而 `TestWebpDecode`
# 是拿真实 WebP 解一次 —— 只测 RIFF 魔数识别不够，那只证明「认出来了」不证明「解得开」。
# 样本按**仓库里的相对路径**摆放（tests\fixtures\...），测试也是按那个相对路径找的，
# 所以这里保持同样的层级，不要拍平。
#
# ⚠️ 2026-09-28 修：原来用 `Get-ChildItem -File`，**只拷顶层文件、不进子目录** ——
#    而 WebP 那边后来改成了目录语料（`tests\fixtures\webp\corpus.json` + 样本），
#    于是包内测试报「找得到 tests\fixtures\webp\corpus.json」→ 整包构建失败。
#    这里改成**递归保持相对层级**地复制。
$fixtureDir = Join-Path $root 'tests\fixtures'
if (Test-Path -LiteralPath $fixtureDir -PathType Container) {
    $fixtureTarget = Join-Path $Package 'tests\fixtures'
    New-Item -ItemType Directory -Force -Path $fixtureTarget | Out-Null
    $fixtureCount = 0
    # -File -Recurse 拿到所有层级的文件；用相对路径还原目录结构（不拍平）
    Get-ChildItem -LiteralPath $fixtureDir -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($fixtureDir.Length).TrimStart('\')
        $dest = Join-Path $fixtureTarget $rel
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        $fixtureCount++
    }
    Write-Output "同步 tests\fixtures（$fixtureCount 个测试样本，含子目录）"
} else {
    Write-Output '警告：找不到 tests\fixtures，包内离线回归会因缺样本失败'
}

# 标题脚本必须先于包内回归同步；根目录副本供源码一致性检查，
# lua 与 mpv 配置副本供播放器实际加载。
foreach ($rel in @('live-title.lua', 'lua\live-title.lua', 'bin\mpv\portable_config\scripts\live-title.lua')) {
    $scriptTarget = Join-Path $Package $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $scriptTarget) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'live-title.lua') -Destination $scriptTarget -Force
    Write-Output ("同步 " + $rel)
}

# 2.8) 版本信息：绿色包才是真正分发出去的东西，没有它用户报问题时无法定位是哪个 commit。
#      包目录不是 git 仓库，所以由这里从**开发仓库**取到信息、生成一个 cs 交给包内编译。
$qrSource = Join-Path $root 'lib\zxing.dll'
if (-not (Test-Path -LiteralPath $qrSource -PathType Leaf)) { throw '缺少受校验的 lib\zxing.dll，无法交付扫码组件' }
New-Item -ItemType Directory -Force -Path (Join-Path $Package 'lib') | Out-Null
Copy-Item -LiteralPath $qrSource -Destination (Join-Path $Package 'lib\zxing.dll') -Force
& (Join-Path $root 'tools\Copy-WebViewAssets.ps1') -SourceRoot $root -Destination $Package
New-Item -ItemType Directory -Force -Path (Join-Path $Package 'tools') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tools\Copy-WebViewAssets.ps1') -Destination (Join-Path $Package 'tools\Copy-WebViewAssets.ps1') -Force
# 历史包只有 portable_config 中的此脚本；显式 --script 从 lua/ 查找。
$resyncSource = Join-Path $Package 'bin\mpv\portable_config\scripts\live-resync.lua'
$resyncTarget = Join-Path $Package 'lua\live-resync.lua'
if ((Test-Path -LiteralPath $resyncSource) -and -not (Test-Path -LiteralPath $resyncTarget)) {
    Copy-Item -LiteralPath $resyncSource -Destination $resyncTarget
}
$infoDir = if ($hasSource) { Join-Path $Package 'src' } else { $Package }
& (Join-Path $root 'tools\Write-BuildInfo.ps1') -Repo $root -Out (Join-Path $infoDir 'build-info.cs')

# 3) 编译（带 src\build.ps1 的包交给它自己跑：编译 + 离线测试 + 自检）
if ($hasSource -and (Test-Path -LiteralPath $packageBuild -PathType Leaf)) {
    & pwsh -NoProfile -File $packageBuild
    if ($LASTEXITCODE -ne 0) { throw "包内 src\build.ps1 失败：$LASTEXITCODE" }
} else {
    $mainArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe", "/win32manifest:$root\app.manifest",
        # 2026-10-16：图标随 exe 走（app.ico 在仓库根；带 src\ 的包会同步到 src\）。
        "/win32icon:$root\app.ico",
        '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Drawing.dll',
        '/r:System.Web.Extensions.dll', '/r:Microsoft.VisualBasic.dll',
(Join-Path $Package 'build-info.cs'), "/r:$root\lib\webview2\Microsoft.Web.WebView2.Core.dll", "/r:$root\lib\webview2\Microsoft.Web.WebView2.WinForms.dll") + $source
    & $compiler @mainArgs
    if ($LASTEXITCODE -ne 0) { throw "封装版编译失败：$LASTEXITCODE" }
}

# 5) 自检
$selfTest = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
$report = Join-Path $Package 'runtime\selftest.txt'
if (Test-Path -LiteralPath $report) {
    Get-Content -LiteralPath $report | Select-String -Pattern 'PORTABLE' | ForEach-Object { Write-Output ('  ' + $_.Line) }
}
if ($selfTest.ExitCode -ne 0) { throw "封装版自检失败：$($selfTest.ExitCode)" }
# 4) 凭据处理（2026-09-27 改，评估报告 §3）：**默认剥离**。
#
# 为什么反过来：原来默认保留、只打印一条警告 —— 只要忘了加 -StripCredentials，
# 发出去的绿色包就带着完整账号登录态（B站 SESSDATA / 抖音 sessionid 等）。
# 打包脚本的默认行为应该是安全的那一侧。自用便携包（「换机不用重扫码」那个用法）
# 显式加 -IncludeCredentials 即可保留。
Write-Output ''
Write-Output '--- 凭据扫描（包内）---'
. (Join-Path $root 'tools\Protect-PackageCredentials.ps1')
Protect-PackageCredentials -Package $Package -IncludeCredentials:$IncludeCredentials
Write-Output ("完成： " + $Package)
