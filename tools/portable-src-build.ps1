# 带源码的绿色包（加固版）在包内自编译用的脚本 —— **模板，由 Build-Portable.ps1 复制过去**。
#
# 为什么要有这个模板（2026-09-28）：
#   原来 `<包>\src\build.ps1` 是**人工维护、只存在于各绿色包内部**的文件，不在 git 里。
#   于是往 `src\` 下加 `App\` 目录时（partial 拆分的另一半），
#   `Build-Hardened.ps1` / `Build-Portable.ps1` 都改了，**它没跟着改** ——
#   包内编译直接报 `CS0426: 类型"LiveHub"中不存在类型名"AclOutcome"`，整包构建不出来。
#   现在改成从这里复制，**跟着仓库走**，不会再漂。
#
# ⚠️ 别再手改包内那份：每次打包都会覆盖它。
#
# 用法：由 Build-Portable.ps1 调用；也可单独跑 `pwsh -File <包>\src\build.ps1` 复现包内构建。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '找不到 .NET Framework C# 编译器。' }

# 2026-09-25 起没有生成器了：启动器 + 分片源码一起交给 csc。
# 它们互相引用（启动器调 DanmakuClientFactory，分片调 LiveHub.RuntimeDir），必须同一编译单元。
#
# ⚠️ 源码列表**动态扫 `src\` 下的每个子目录**，不写死目录名 ——
#    所以以后再加 `src\Foo\` 这种目录，这里**自动生效**，不需要改任何构建脚本。
#    （写死过一次，代价是整包构建失败，见文件头。）
$exe = Join-Path $root '看直播.exe'
$srcRoot = $PSScriptRoot
$source = @((Join-Path $srcRoot 'live-hub-v2.cs'))
Get-ChildItem -LiteralPath $srcRoot -Directory | Sort-Object Name | ForEach-Object {
    $source += @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
}
$source = @($source | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
foreach ($f in $source) { if (-not (Test-Path -LiteralPath $f)) { throw "缺少源文件：$f" } }
if ($source.Count -lt 2) { throw "只找到 $($source.Count) 个源文件，src\ 布局可能不对" }

# build-info.cs（版本号）由打包脚本生成，有就一起编
$versionFile = Join-Path $srcRoot 'build-info.cs'
$mainArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe", "/win32manifest:$srcRoot\app.manifest",
    '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll', '/r:Microsoft.VisualBasic.dll', "/r:$root\lib\webview2\Microsoft.Web.WebView2.Core.dll", "/r:$root\lib\webview2\Microsoft.Web.WebView2.WinForms.dll") +
    @(if (Test-Path -LiteralPath $versionFile) { ,$versionFile } else { @() }) +
    # 图标（2026-10-16）：与 app.manifest 同处 src\。**有才用** —— 老包没带这个文件也能照常构建。
    @(if (Test-Path -LiteralPath (Join-Path $srcRoot 'app.ico')) { ,("/win32icon:" + (Join-Path $srcRoot 'app.ico')) } else { @() }) +
    $source
Copy-Item -LiteralPath (Join-Path $srcRoot 'app.config') -Destination ($exe + '.config') -Force
Copy-Item -LiteralPath (Join-Path $srcRoot 'THIRD_PARTY_NOTICES.md') -Destination $root -Force
Copy-Item -LiteralPath (Join-Path $srcRoot 'licenses') -Destination $root -Recurse -Force
& (Join-Path $root 'tools\Copy-WebViewAssets.ps1') -SourceRoot $root -Destination $root
& $compiler @mainArgs
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败：$LASTEXITCODE" }

# 离线回归：tests\*.cs 是同一个 partial class 的两半，必须一起编
$testSource = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
foreach ($f in $testSource) { if (-not (Test-Path -LiteralPath $f)) { throw "缺少测试源文件：$f" } }
$testExe = Join-Path $root 'tests\HardeningTests.exe'
& $compiler /nologo /target:exe /optimize+ "/out:$testExe" @testSource
if ($LASTEXITCODE -ne 0) { throw "测试程序编译失败：$LASTEXITCODE" }
& $testExe
if ($LASTEXITCODE -ne 0) { throw "离线测试失败：$LASTEXITCODE" }

# 自检（--self-test 覆盖弹幕分片、配置往返、DPI 等）
$selfTest = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
if ($selfTest.ExitCode -ne 0) { throw "启动器自检失败：$($selfTest.ExitCode)" }

Write-Output '加固版编译、离线测试、自检均通过。'
Write-Output ("  源文件 $($source.Count) 个（含 src\ 下所有子目录）")
