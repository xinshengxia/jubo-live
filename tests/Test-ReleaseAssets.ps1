param([string]$Workspace = '', [string]$Source = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if (-not $Workspace) { $Workspace = Join-Path $env:TEMP ('livehub-release-assets-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $Workspace) { throw '测试工作目录必须尚不存在' }
$validation = Join-Path $Workspace 'validation'
$staging = Join-Path $Workspace 'staging'
New-Item -ItemType Directory -Path $validation, $staging -Force | Out-Null
$validationExe = Join-Path $validation '看直播.exe'
$exePath = Join-Path $staging '看直播.exe'
[IO.File]::WriteAllText($validationExe, 'synthetic executable')
Copy-Item -LiteralPath (Join-Path $Source 'app.config') -Destination ($validationExe + '.config')
Copy-Item -LiteralPath (Join-Path $Source 'THIRD_PARTY_NOTICES.md') -Destination $staging
Copy-Item -LiteralPath (Join-Path $Source 'licenses') -Destination $staging -Recurse
& (Join-Path $Source 'tools\Copy-WebViewAssets.ps1') -SourceRoot $Source -Destination $staging

# 执行发布脚本的实际复制语句和文件筛选块，只操作上面的合成目录。
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Source 'tools\Publish-Release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw '发布脚本解析失败' }
$copies = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Copy-Item' -and $node.Extent.Text.Contains('$validationExe')
}, $true))
if (-not $copies.Count) { throw '未找到发布产物复制语句，回归不能空转' }
foreach ($copy in $copies) { . ([scriptblock]::Create($copy.Extent.Text)) }
if (-not (Test-Path -LiteralPath ($exePath + '.config'))) { throw 'FAIL: 发布 staging 缺少 exe.config' }
if ((Get-FileHash -LiteralPath ($exePath + '.config')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Source 'app.config')).Hash) { throw 'FAIL: 发布 config 与源码不一致' }
Write-Output 'PASS: 发布复制包含 exe.config，且与源码配置逐字节一致'

foreach ($name in @('allowTopFiles', 'allowDirs', 'userDataFiles')) {
    $assignment = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq ('$' + $name) }, $true)
    if ($assignment.Count -ne 1) { throw "未找到唯一的发布筛选变量：$name" }
    . ([scriptblock]::Create($assignment[0].Extent.Text))
}
$relativeFunction = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-RelPath' }, $true)
. ([scriptblock]::Create($relativeFunction[0].Extent.Text))
$selection = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Extent.Text.Contains('$allFiles.Add($f)')
}, $true)
if ($selection.Count -ne 1) { throw '未找到唯一的发布文件筛选块' }
$IncludeSources = $false; $IncludeUserData = $false
$gitPrefix = (Join-Path $staging '.git') + [IO.Path]::DirectorySeparatorChar
$skippedSources = [Collections.Generic.List[string]]::new()
$skippedUserData = [Collections.Generic.List[string]]::new()
$allFiles = [Collections.Generic.List[object]]::new()
. ([scriptblock]::Create($selection[0].Extent.Text))
if (-not ($allFiles | Where-Object Name -EQ '看直播.exe.config')) { throw 'FAIL: 默认发布白名单排除了 exe.config' }
foreach ($sdkFile in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','lib/webview2/x64/WebView2Loader.dll','lib/webview2/x86/WebView2Loader.dll')) {
    if (-not ($allFiles | Where-Object { (Get-RelPath $staging $_.FullName) -eq $sdkFile })) { throw "FAIL: 默认发布白名单排除了 $sdkFile" }
}
Write-Output 'PASS: 默认运行包筛选保留 exe.config'
foreach ($required in @('THIRD_PARTY_NOTICES.md','licenses/SOURCES.json','licenses/douyinLive-LICENSE.txt',
    'licenses/AllLive-LICENSE.txt','licenses/blivedm-LICENSE.txt','licenses/libwebp-COPYING.txt','licenses/libwebp-PATENTS.txt','licenses/libwebp-AUTHORS.txt','licenses/WebView2-SDK-LICENSE.txt','licenses/WebView2-SDK-NOTICE.txt')) {
    if (-not ($allFiles | Where-Object { (Get-RelPath $staging $_.FullName) -eq $required })) { throw "FAIL: 默认发布白名单遗漏 $required" }
}
foreach ($record in (Get-Content -LiteralPath (Join-Path $Source 'licenses/SOURCES.json') -Raw | ConvertFrom-Json).files) {
    if (-not ($allFiles | Where-Object { (Get-RelPath $staging $_.FullName) -eq $record.path })) { throw "FAIL: 默认发布白名单遗漏来源清单中的许可 $($record.path)" }
}
Write-Output 'PASS: 默认运行包包含声明与完整许可原文'

function New-SyntheticArchive([string]$name, [bool]$includeConfig, [bool]$validConfig, [string]$omitNotice = '', [switch]$TamperLicense, [string]$BundleCase = '') {
    $zipPath = Join-Path $Workspace $name
    $config = if ($validConfig) { [IO.File]::ReadAllText($exePath + '.config') } else { '<configuration />' }
    $contents = [ordered]@{ '看直播.exe' = 'synthetic executable' }
    if ($includeConfig) { $contents['看直播.exe.config'] = $config }
    $contents['THIRD_PARTY_NOTICES.md'] = [IO.File]::ReadAllBytes((Join-Path $Source 'THIRD_PARTY_NOTICES.md'))
    foreach ($sdkFile in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','lib/webview2/x64/WebView2Loader.dll','lib/webview2/x86/WebView2Loader.dll')) {
        $contents[$sdkFile] = [IO.File]::ReadAllBytes((Join-Path $staging $sdkFile))
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Source 'licenses') -File -Recurse) {
        $relative = 'licenses/' + $file.FullName.Substring((Join-Path $Source 'licenses').Length + 1).Replace('\','/')
        $contents[$relative] = [IO.File]::ReadAllBytes($file.FullName)
    }
    if ($omitNotice) { $contents.Remove($omitNotice) }
    if ($TamperLicense) { $contents['licenses/douyinLive-LICENSE.txt'] = 'tampered notice with internally consistent archive hashes' }
    $components = @()
    if ($BundleCase) {
        $contents['LICENSE'] = 'Synthetic fixture license'
        $contents['bin/fixture/tool.txt'] = 'Synthetic tool'
        $bytes = [Text.Encoding]::UTF8.GetBytes($contents['bin/fixture/tool.txt'])
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        $treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("tool.txt`n$hash`n")))
        $bundle = @{name='fixture';targetDir='bin/fixture';treeSha256=$treeHash;files=@(@{path='tool.txt';size=$bytes.Length;sha256=$hash})}
        $components = @(@{name='fixture';target='bin/fixture';kind='bundle';sha256=$treeHash;size=$bytes.Length})
        if ($BundleCase -eq 'tree') { $components[0].sha256 = '0' * 64 }
        if ($BundleCase -eq 'member') { $contents['bin/fixture/tool.txt'] = 'Modified member with consistent artifact hash' }
        if ($BundleCase -eq 'omitted') { $components = @(@{name='unknown';kind='file';target='bin/fixture/tool.txt';sha256=$hash;size=$bytes.Length}) }
        $contents['deps.lock.json'] = @{schema='livehub-deps-lock/1';components=@();bundles=@($bundle)} | ConvertTo-Json -Depth 8
    }
    $records = foreach ($path in $contents.Keys) {
        $bytes = if ($contents[$path] -is [byte[]]) { $contents[$path] } else { [Text.Encoding]::UTF8.GetBytes($contents[$path]) }
        @{ path = $path; size = $bytes.Length; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
    }
    $manifest = @{ schema = 'livehub-release-manifest/1'; commit = @{ full = ('a' * 40) }; buildId = '1.0.0+aaaaaaaaaaaa';
        tests = @{ failed = 0 }; buildExitCode = 0; variant='full'; artifacts = @($records); components = $components; credentials = @{ mode = 'strip' } }
    $contents['manifest.json'] = $manifest | ConvertTo-Json -Depth 8
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in $contents.Keys) {
            $bytes = if ($contents[$path] -is [byte[]]) { $contents[$path] } else { [Text.Encoding]::UTF8.GetBytes($contents[$path]) }
            $stream = $archive.CreateEntry($path).Open()
            try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    [IO.File]::WriteAllText($zipPath + '.sha256', (Get-FileHash -LiteralPath $zipPath).Hash + '  ' + $name)
    return $zipPath
}
$verifier = Join-Path $Source 'tools\Verify-ReleaseArchive.ps1'
& $verifier -Zip (New-SyntheticArchive 'valid.zip' $true $true)
foreach ($case in @(@('missing.zip', $false, $true), @('invalid.zip', $true, $false))) {
    $rejected = $false
    try { & $verifier -Zip (New-SyntheticArchive $case[0] $case[1] $case[2]) } catch { $rejected = $true }
    if (-not $rejected) { throw "FAIL: 归档验收接受了缺失/无效运行配置：$($case[0])" }
}
Write-Output 'PASS: 有效归档通过，缺失或无效运行配置的归档被拒绝'
foreach ($omitted in @('THIRD_PARTY_NOTICES.md','licenses/douyinLive-LICENSE.txt','licenses/blivedm-LICENSE.txt','licenses/libwebp-PATENTS.txt','licenses/WebView2-SDK-LICENSE.txt','licenses/WebView2-SDK-NOTICE.txt','Microsoft.Web.WebView2.Core.dll','lib/webview2/x64/WebView2Loader.dll')) {
    $rejected = $false
    try { & $verifier -Zip (New-SyntheticArchive ('missing-notice-' + [guid]::NewGuid().ToString('N') + '.zip') $true $true $omitted) } catch { $rejected = $true }
    if (-not $rejected) { throw "FAIL: 归档验收接受了缺失许可：$omitted" }
}
$rejected = $false
try { & $verifier -Zip (New-SyntheticArchive 'tampered-license.zip' $true $true -TamperLicense) } catch { $rejected = $true }
if (-not $rejected) { throw 'FAIL: 归档验收接受了与上游快照哈希不同的许可原文' }
Write-Output 'PASS: 缺失声明/许可/专利授权或修改许可原文的归档均被拒绝'
& $verifier -Zip (New-SyntheticArchive 'valid-bundle.zip' $true $true -BundleCase 'valid')
foreach ($case in @('tree','member','omitted')) {
    $rejected = $false
    try { & $verifier -Zip (New-SyntheticArchive ($case + '-bundle.zip') $true $true -BundleCase $case) } catch { $rejected = $true }
    if (-not $rejected) { throw ('FAIL: inconsistent locked bundle accepted: ' + $case) }
}
Write-Output 'PASS: valid locked bundle accepted; modified tree, member and missing component rejected'
