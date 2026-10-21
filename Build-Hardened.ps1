# Build-Hardened.ps1 —— 编译启动器 → 跑离线加固回归 → 自检（退出码非 0 = 失败）
#
#   pwsh -File .\Build-Hardened.ps1                       # 正常构建，覆盖 .\看直播-mpv.exe
#   pwsh -File .\Build-Hardened.ps1 -Out .\artifacts\test-build\app.exe  # 构建到别处（目标 exe 被『看直播』窗口占用时用）
#
# 2026-09-20：① 编译器改为优先 64 位 csc（32 位 csc 对超大源文件更吃力）；
#             ② 增加 -Out，目标被占用时给人话错误而不是 csc 的 CS0016；
#             ③ 把刚编出的 exe 路径显式传给测试（HardeningTests 支持 --exe）。
param(
    [string]$Out = '',
    # 离线回归的 JSON 报告落点（评估报告 §2：要机器可读的测试摘要，别只留一屏文字）。
    # 不传就只打印；传了就额外得到 passed / failed / skipped / 每条用例的状态与耗时。
    [string]$Report = '',
    [switch]$Headless
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path $root '看直播-mpv.exe' }
# 2026-09-25 起**不再有生成器**：单文件约束解除，弹幕分片源码直接参与编译。
#   · 启动器 = live-hub-v2.cs
#   · 其余分片 = src\ 下每个子目录的 *.cs
# 一次把所有 .cs 交给 csc（它们互相引用，必须同一编译单元）。
#
# ⚠️ 2026-09-28 改成**动态扫 src\ 下的子目录**，不写死 Danmaku/App ——
#    写死过一次：加 `src\App\` 时改了三处脚本，却漏了包内那份 `src\build.ps1`，
#    导致「看直播（加固版）」整包构建不出来（CS0426 AclOutcome 找不到）。
#    以后再加 `src\Foo\` 这类目录，这里**自动生效**。
$source = @((Join-Path $root 'live-hub-v2.cs'))
Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory | Sort-Object Name | ForEach-Object {
    $source += @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
}
if ($source.Count -lt 2) { throw "只找到 $($source.Count) 个源文件，src\ 布局可能不对" }
foreach ($f in $source) { if (-not (Test-Path -LiteralPath $f)) { throw "缺少源文件：$f" } }
$testSource = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
foreach ($f in $testSource) { if (-not (Test-Path -LiteralPath $f)) { throw "缺少测试源文件：$f" } }
$testExe = Join-Path (Split-Path -Parent $exe) 'tests\HardeningTests.exe'

function Resolve-Csc {
    # 优先 64 位编译器，再退 32 位；两者都没有就直接说清楚（Roslyn 走 dotnet 需要 net48 参考程序集，不在这里猜）
    foreach ($candidate in @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw '找不到 .NET Framework C# 编译器（csc.exe），无法构建。'
}
$compiler = Resolve-Csc

# 目标 exe 被占用时（『看直播』窗口开着）先给人话错误
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    try { $probe = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None'); $probe.Dispose() }
    catch { throw "目标 exe 正被占用，先关掉『看直播』窗口，或用 -Out 构建到别处：$exe" }
}
$outDir = Split-Path -Parent $exe
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

# 两条构建路径共享同一版本来源。
$versionFile = Join-Path $root 'obj\legacy\build-info.cs'
& (Join-Path $root 'tools\Write-BuildInfo.ps1') -Repo $root -Out $versionFile

$antdDll = Join-Path $root 'lib\antdui\AntdUI.dll'
if (-not (Test-Path -LiteralPath $antdDll -PathType Leaf)) { throw "缺少 AntdUI.dll：$antdDll" }
$mainArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe", "/win32manifest:$root\app.manifest",
    # 2026-10-16：图标随 exe 走（app.ico 在仓库根）。此前 exe 里**没有任何图标资源**，
    # 图标全靠快捷方式 IconLocation 指向外部 mpv.exe —— 那个文件一被清理，任务栏就
    # 回落成系统空白图标（用户实测踩到）。嵌进来之后就与外部文件无关了。
    "/win32icon:$root\app.ico",
    '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll', '/r:Microsoft.VisualBasic.dll',
$versionFile, "/r:$root\lib\webview2\Microsoft.Web.WebView2.Core.dll", "/r:$root\lib\webview2\Microsoft.Web.WebView2.WinForms.dll", "/r:$antdDll") + $source
& (Join-Path $root 'tools\Copy-WebViewAssets.ps1') -SourceRoot $root -Destination $outDir
Copy-Item -LiteralPath (Join-Path $root 'app.config') -Destination ($exe + '.config') -Force
& $compiler @mainArgs
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败：$LASTEXITCODE" }
Copy-Item -LiteralPath $antdDll -Destination (Join-Path $outDir 'AntdUI.dll') -Force
# -Out 换目录时必须把精灵图一起带过去：离线测试里有一组会从**目标 exe 旁边**找
# emoji-sheet.png，不带过去就会以「找不到 emoji-sheet.png」失败 ——
# 而 -Out 的卖点恰恰是「目标被占用时构建到别处」，那样等于这个功能不成立（评估报告实测）。
if ($outDir -ne $PSScriptRoot) {
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $outDir -Force
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $outDir -Recurse -Force
    # 与 SDK 产物布局保持一致；mpv 的显式 --script 参数需要这个文件。
    $luaTo = Join-Path $outDir 'lua'
    New-Item -ItemType Directory -Force -Path $luaTo | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'live-title.lua') -Destination (Join-Path $luaTo 'live-title.lua') -Force
    $qrFrom = Join-Path $root 'lib\zxing.dll'
    if (Test-Path -LiteralPath $qrFrom -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Join-Path $outDir 'lib') | Out-Null
        Copy-Item -LiteralPath $qrFrom -Destination (Join-Path $outDir 'lib\zxing.dll') -Force
    }
    foreach ($asset in @('emoji-sheet.png', 'emoji-sheet.txt')) {
        $from = Join-Path $PSScriptRoot $asset
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $outDir $asset) -Force }
    }
    # 界面字体同理：程序从 **exe 同级的 fonts\** 目录注册 HarmonyOS Sans SC。
    # 不带过去的话，-Out 构建出来的 exe 会静默退回系统雅黑 —— 界面还能用，
    # 但不是设计的那套字体，而且没有任何报错，很难发现。
    # 测试样本同理：离线回归里有几条要拿真实 WebP 文件解一次
    #
    # ⚠️ 2026-09-28 修：原来用 `Get-ChildItem -File`，**只拷顶层、不进子目录** ——
    #    而 WebP 后来改成目录语料（`tests\fixtures\webp\corpus.json` + 样本），
    #    于是 `-Out` 出来的目录跑离线回归会报「找得到 tests\fixtures\webp\corpus.json」。
    #    与 Build-Portable.ps1 同一处坑，改法一致：**递归且保持相对层级**。
    $fixtureFrom = Join-Path $PSScriptRoot 'tests\fixtures'
    if (Test-Path -LiteralPath $fixtureFrom -PathType Container) {
        $fixtureTo = Join-Path $outDir 'tests\fixtures'
        New-Item -ItemType Directory -Force -Path $fixtureTo | Out-Null
        Get-ChildItem -LiteralPath $fixtureFrom -File -Recurse | ForEach-Object {
            $rel = $_.FullName.Substring($fixtureFrom.Length).TrimStart('\')
            $dest = Join-Path $fixtureTo $rel
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $destDir | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        }
    }
    $fontFrom = Join-Path $PSScriptRoot 'fonts'
    if (Test-Path -LiteralPath $fontFrom -PathType Container) {
        $fontTo = Join-Path $outDir 'fonts'
        New-Item -ItemType Directory -Force -Path $fontTo | Out-Null
        # -LiteralPath 不展开通配符，逐个复制
        Get-ChildItem -LiteralPath $fontFrom -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $fontTo $_.Name) -Force
        }
    }
}
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败：$LASTEXITCODE" }
Write-Output ('编译完成：' + $exe)

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($testExe)) | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'app.config') -Destination ($testExe + '.config') -Force
& $compiler /nologo /target:exe /optimize+ "/win32manifest:$root\app.manifest" "/out:$testExe" @testSource
if ($LASTEXITCODE -ne 0) { throw "测试程序编译失败：$LASTEXITCODE" }

# 离线回归（2026-09-27 改造，评估报告 §2）：
#   · 完整输出**同时**落一份日志 —— 原来只打印构建输出最后 6 行，第一次跑连失败项都看不见；
#   · --src 把源码根显式传给测试（-Out 到别处时，扫源码的用例不会再误报「空转」）；
#   · 失败时把**第一条 FAIL / 失败用例名 / 完整日志路径**顶到最前面。
$logDir = Join-Path $outDir 'runtime\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir ('offline-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$testArgs = @('--exe', $exe, '--src', $root)
if ($Headless) { $testArgs += '--headless' }
if ($Report) { $testArgs += @('--report', $Report) }
. (Join-Path $root 'tools/Invoke-BoundedProcess.ps1')
Invoke-BoundedProcess -FilePath $testExe -Arguments ($testArgs + @('--verbose')) -Log $logPath -TimeoutSeconds 240
$testOut = @(Get-Content -LiteralPath $logPath -Encoding utf8)
$testOut | ForEach-Object { Write-Output $_ }
Write-Output ('离线测试日志： ' + $logPath)

& (Join-Path $root 'tests\Test-PackageCredentials.ps1')
& (Join-Path $root 'tests\Test-ReleaseAssets.ps1')

Invoke-BoundedProcess -FilePath $exe -Arguments @('--self-test') -Log (Join-Path $logDir 'selftest-process.log') -TimeoutSeconds 30
Write-Output '开发版编译、离线测试、自检均通过。'
