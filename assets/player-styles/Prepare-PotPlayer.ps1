param([Parameter(Mandatory)][string]$Player)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

try {
    $playerFile = Get-Item -LiteralPath $Player
    if ($playerFile.Name -ine 'PotPlayerMini64.exe' -and $playerFile.Name -ine 'PotPlayer64.exe') {
        throw '定制外观需要 64 位 PotPlayer。'
    }
    $installed = $playerFile.Directory.FullName
    $source = Join-Path $PSScriptRoot 'fluent-glass'
    $defaultSkin = Get-Item -LiteralPath (Join-Path $installed 'Skins/Default.dsf')
    $mini = Get-Item -LiteralPath (Join-Path $installed 'PotPlayerMini64.exe')
    $libraries = @(Get-ChildItem -LiteralPath $installed -File -Filter '*.dll' | Sort-Object Name)
    $files = @($mini, $defaultSkin) + $libraries
    $recipeHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'fluent-glass.sha256')).Hash
    $keyParts = @($installed, $recipeHash) + @($files | ForEach-Object {
        $_.Name + ':' + $_.Length + ':' + $_.LastWriteTimeUtc.Ticks
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($keyParts -join '|')))).Replace('-','') }
    finally { $sha.Dispose() }

    $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JuboLive/player-styles'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $runtime = Join-Path $cache $key
    $exe = Join-Path $runtime 'PotPlayerMini64.exe'
    $mutex = [Threading.Mutex]::new($false, "Local\JuboLive-FluentGlass-$key")
    $held = $false
    try {
        try { $held = $mutex.WaitOne([TimeSpan]::FromMinutes(10)) }
        catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw '另一场皮肤准备尚未完成，请稍后重试。' }
        if (-not [IO.File]::Exists($exe)) {
            $stage = Join-Path $cache ([Guid]::NewGuid().ToString('N'))
            [IO.Directory]::CreateDirectory($stage) | Out-Null
            try {
                foreach ($record in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fluent-glass.sha256') -Encoding UTF8) {
                    $digest, $name = $record -split '  ', 2
                    if ($digest -notmatch '^[0-9a-f]{64}$' -or $name -notmatch '^[A-Za-z0-9_./-]+$' -or $name.Contains('..') -or $name.StartsWith('/')) { throw '皮肤资源清单无效。' }
                    $file = Join-Path $source $name
                    if ((Get-FileHash -LiteralPath $file).Hash -ne $digest) { throw '皮肤资源校验失败，请重新解压软件。' }
                    $target = Join-Path $stage $name
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                    [IO.File]::Copy($file,$target)
                }
                & (Join-Path $stage 'tools/install.ps1') -PlayerDirectory $installed -PrepareOnly -Json *> $null
                $built = Join-Path $stage '.runtime'
                foreach ($required in @('PotPlayerMini64.exe','PotPlayer64.dll','Skins/FluentGlass.dsf','PotPlayerMini64.ini')) {
                    if (-not [IO.File]::Exists((Join-Path $built $required))) { throw '定制播放器生成不完整。' }
                }
                # install.json records build-only absolute paths to the temporary Python environment.
                [IO.File]::Delete((Join-Path $built 'install.json'))
                [IO.Directory]::Move($built,$runtime)
            } finally {
                $resolved = [IO.Path]::GetFullPath($stage)
                if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($cache) -and [IO.Path]::GetFileName($resolved) -match '^[0-9a-f]{32}$') {
                    Remove-Item -LiteralPath $resolved -Recurse -Force
                }
            }
        }
        [Console]::Write($exe)
    } finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
