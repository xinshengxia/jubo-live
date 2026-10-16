param([Parameter(Mandatory)][string]$Zip, [string]$ExpectedCommit = '')
$ErrorActionPreference = 'Stop'
$expectedHash = (Get-Content -LiteralPath ($Zip + '.sha256') -Raw).Trim().Split(' ')[0]
if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash -ne $expectedHash) { throw 'ZIP SHA-256 不匹配' }
$stream = [IO.File]::OpenRead($Zip)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $names = @{}
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name -match '^[/\\]|\\|(^|/)\.\.(/|$)|:' -or $names.ContainsKey($name)) { throw "危险或重复的 ZIP 条目：$name" }
            $names[$name] = $entry
        }
        if (-not $names.ContainsKey('manifest.json')) { throw 'ZIP 缺少 manifest.json' }
        $reader = [IO.StreamReader]::new($names['manifest.json'].Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.schema -ne 'livehub-release-manifest/1' -or $manifest.commit.full -notmatch '^[a-f0-9]{40}$') { throw '发布清单来源无效' }
        if ($ExpectedCommit -and $manifest.commit.full -ne $ExpectedCommit) { throw '发布 commit 不匹配' }
        if ($manifest.buildId -match 'dirty|nogit' -or -not $manifest.buildId.Contains($manifest.commit.full.Substring(0,12))) { throw 'build ID 与发布 commit 不匹配' }
        if ($manifest.tests.failed -ne 0 -or $manifest.buildExitCode -ne 0) { throw '清单中的构建/测试未通过' }
        $listed = @{}
        foreach ($item in $manifest.artifacts) {
            if ($listed.ContainsKey($item.path) -or -not $names.ContainsKey($item.path)) { throw "清单条目重复或缺失：$($item.path)" }
            $listed[$item.path] = $true
            $entry = $names[$item.path]
            if ($entry.Length -ne $item.size) { throw "大小不匹配：$($item.path)" }
            $content = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($sha.ComputeHash($content)) } finally { $content.Dispose(); $sha.Dispose() }
            if ($actual -ne $item.sha256) { throw "文件哈希不匹配：$($item.path)" }
        }
        foreach ($name in $names.Keys) {
            if ($name -ne 'manifest.json' -and -not $listed.ContainsKey($name)) { throw "ZIP 中有未登记文件：$name" }
            if ($manifest.credentials.mode -eq 'strip' -and $name -match '(?i)(^|/)(profiles?|[^/]*-(login|verify)-profile)(/|$)|cookies[^/]*\.txt(\.json)?$|cookie[^/]*\.credential\.tmp$') { throw "ZIP 中残留凭据：$name" }
        }
        $componentNames = @{}
        $lock = $null
        if (@($manifest.components).Count) {
            if (-not $names.ContainsKey('deps.lock.json')) { throw '锁定组件缺少包内依赖清单' }
            $reader = [IO.StreamReader]::new($names['deps.lock.json'].Open())
            try { $lock = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($lock.schema -ne 'livehub-deps-lock/1') { throw '包内依赖锁格式无效' }
        }
        foreach ($component in $manifest.components) {
            if ($componentNames.ContainsKey($component.name)) { throw '重复锁定组件' }
            $componentNames[$component.name] = $true
            if ($component.kind -eq 'file') {
                $locked = @($lock.components | Where-Object name -EQ $component.name)
                if ($locked.Count -ne 1 -or $locked[0].target -ne $component.target -or $locked[0].sha256 -ne $component.sha256 -or $locked[0].size -ne $component.size) { throw '组件与包内依赖锁不一致' }
                $record = @($manifest.artifacts | Where-Object path -EQ $component.target)
                if ($record.Count -ne 1 -or $record[0].sha256 -ne $component.sha256 -or $record[0].size -ne $component.size) {
                    throw "锁定组件未入包或哈希/大小不一致：$($component.target)"
                }
            } elseif ($component.kind -eq 'bundle') {
                $locked = @($lock.bundles | Where-Object name -EQ $component.name)
                if ($locked.Count -ne 1 -or $locked[0].targetDir -ne $component.target) { throw '目录组件与包内依赖锁不一致' }
                $prefix = $component.target.TrimEnd('/') + '/'
                $records = @($manifest.artifacts | Where-Object { $_.path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) })
                if ($records.Count -ne @($locked[0].files).Count) { throw '目录组件文件集合不一致' }
                $lines = [Collections.Generic.List[string]]::new(); [long]$total = 0
                foreach ($file in $locked[0].files | Sort-Object path) {
                    $record = @($records | Where-Object path -EQ ($prefix + $file.path))
                    if ($record.Count -ne 1 -or $record[0].sha256 -ne $file.sha256 -or $record[0].size -ne $file.size) { throw '目录组件内部文件与依赖锁不一致' }
                    $lines.Add($file.path.ToLowerInvariant() + "`n" + $file.sha256.ToUpperInvariant() + "`n"); $total += $file.size
                }
                $treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join ''))))
                if ($treeHash -ne $locked[0].treeSha256 -or $treeHash -ne $component.sha256 -or $total -ne $component.size) { throw '目录组件整体哈希或大小不一致' }
            } else { throw '未知组件类型' }
        }
        if ($lock) {
            foreach ($required in @('LICENSE','deps.lock.json')) { if (-not $listed.ContainsKey($required)) { throw '实际发布包缺少许可或依赖锁' } }
            if ($manifest.variant -notin @('full','lite')) { throw '发布变体无效' }
            $expectedComponents = @($lock.components | Where-Object { $_.name -ne 'potplayer' -and ($manifest.variant -eq 'full' -or $_.name -ne 'mpv') })
            $expectedBundles = @($lock.bundles | Where-Object { $_.name -ne 'potplayer-dist' -and ($manifest.variant -eq 'full' -or $_.name -ne 'mpv-dist') })
            foreach ($required in @($expectedComponents) + @($expectedBundles)) { if (-not $componentNames.ContainsKey($required.name)) { throw '发布包遗漏锁定组件' } }
            if ($componentNames.Count -ne ($expectedComponents.Count + $expectedBundles.Count)) { throw '发布包包含意外组件' }
        }
        if (-not $listed.ContainsKey('看直播.exe')) { throw 'ZIP 缺少启动器' }
        if (-not $listed.ContainsKey('看直播.exe.config')) { throw 'ZIP 缺少启动器运行配置' }
        foreach ($sdkFile in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','lib/webview2/x64/WebView2Loader.dll','lib/webview2/x86/WebView2Loader.dll')) {
            if (-not $listed.ContainsKey($sdkFile) -or $names[$sdkFile].Length -eq 0) { throw "ZIP 缺少网页辅助组件：$sdkFile" }
        }
        $requiredNotices = @('THIRD_PARTY_NOTICES.md', 'licenses/SOURCES.json',
            'licenses/douyinLive-LICENSE.txt', 'licenses/AllLive-LICENSE.txt', 'licenses/blivedm-LICENSE.txt',
            'licenses/libwebp-COPYING.txt', 'licenses/libwebp-PATENTS.txt', 'licenses/libwebp-AUTHORS.txt',
            'licenses/WebView2-SDK-LICENSE.txt', 'licenses/WebView2-SDK-NOTICE.txt')
        foreach ($notice in $requiredNotices) {
            if (-not $listed.ContainsKey($notice) -or $names[$notice].Length -eq 0) { throw "ZIP 缺少第三方声明/许可：$notice" }
        }
        $reader = [IO.StreamReader]::new($names['licenses/SOURCES.json'].Open())
        try { $licenseSources = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($licenseSources.schema -ne 'livehub-third-party-sources/1') { throw '第三方许可来源清单格式无效' }
        foreach ($notice in $requiredNotices | Where-Object { $_ -like 'licenses/*.txt' }) {
            if (@($licenseSources.files | Where-Object path -EQ $notice).Count -ne 1) { throw "许可来源清单缺少必需条目：$notice" }
        }
        foreach ($sourceRecord in $licenseSources.files) {
            $notice = [string]$sourceRecord.path
            $artifactRecord = @($manifest.artifacts | Where-Object path -EQ $notice)
            if ($notice -notmatch '^licenses/[^/]+\.txt$' -or $artifactRecord.Count -ne 1 -or
                $sourceRecord.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
                $artifactRecord[0].sha256 -ne $sourceRecord.sha256) { throw "第三方许可原文与来源哈希不一致：$notice" }
        }
        $reader = [IO.StreamReader]::new($names['看直播.exe.config'].Open())
        try { [xml]$config = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $settings = $config.configuration.'System.Windows.Forms.ApplicationConfigurationSection'.add
        if (@($settings | Where-Object { $_.key -eq 'DpiAwareness' -and $_.value -eq 'PerMonitorV2' }).Count -ne 1 -or
            @($settings | Where-Object { $_.key -eq 'EnableWindowsFormsHighDpiAutoResizing' -and $_.value -eq 'false' }).Count -ne 1 -or
            $config.configuration.startup.supportedRuntime.sku -ne '.NETFramework,Version=v4.8') { throw 'ZIP 运行配置缺少 net48/DPI 策略' }
        Write-Output "PASS: ZIP/清单/commit/build ID/全部 $($listed.Count) 个文件哈希及凭据策略一致；GUI 与在线平台状态见清单"
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
