param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination, [switch]$ToolsOnly)
$ErrorActionPreference = 'Stop'
# 只供离线解析/启动计划验收：空工具占位文件不可用于真实播放。
foreach ($relative in @('bin\mpv\mpv.exe', 'bin\PotPlayer\PotPlayerMini64.exe', 'bin\yt-dlp.exe')) {
    $path = Join-Path $Destination $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    if (-not (Test-Path -LiteralPath $path)) { [IO.File]::WriteAllBytes($path, [byte[]]@()) }
}
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'lua') | Out-Null
Copy-Item -LiteralPath (Join-Path $Source 'live-title.lua') -Destination (Join-Path $Destination 'lua\live-title.lua') -Force
if ($ToolsOnly) { return }
foreach ($asset in @('emoji-sheet.png', 'emoji-sheet.txt', 'fonts', 'lib', 'tests\fixtures', 'THIRD_PARTY_NOTICES.md', 'licenses')) {
    $from = Join-Path $Source $asset
    if (Test-Path -LiteralPath $from) {
        $target = Join-Path $Destination $asset
        if ((Get-Item -LiteralPath $from).PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Get-ChildItem -LiteralPath $from -Recurse -File | ForEach-Object {
                $to = Join-Path $target $_.FullName.Substring($from.Length).TrimStart('\')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $to -Force
            }
        } else { Copy-Item -LiteralPath $from -Destination $target -Force }
    }
}
