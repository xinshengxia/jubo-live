# Static preflight for public source trees and final release archives.
[CmdletBinding(DefaultParameterSetName='Directory')]
param(
    [Parameter(Mandatory, ParameterSetName='Directory')][string]$Directory,
    [Parameter(Mandatory, ParameterSetName='Archive')][string]$Archive
)
$ErrorActionPreference = 'Stop'
$issues = [Collections.Generic.List[string]]::new()
$script:checkedFiles = 0
$patterns = [ordered]@{
    credentials = '(?i)(?<![A-Za-z0-9])(?:sk-(?!ssh-|ecdsa-)(?:proj-)?[A-Za-z0-9_-]{25,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[A-Z0-9]{16})'
    cookies = '(?i)(?:SESSDATA|bili_jct|sessionid|sid_guard|ttwid|access_token|refresh_token)\s*=\s*[A-Za-z0-9_%+/=-]{25,}'
    privateKey = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=\r\n]{80,}-----END'
    userDirectory = '(?i)(?:[A-Z]:[\\/]+Users[\\/]+[^\\/\s"<>]+|/(?:home|Users)/[^/\s"<>]+)'
    hostName = '\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{5,}\b'
}
function Add-Issue([string]$path, [string]$rule) { $issues.Add($path + ': ' + $rule) }
function Test-Content([byte[]]$bytes, [string]$path) {
    $script:checkedFiles++
    $leaf = ($path -split '/')[-1]
    if ($leaf -match '(?i)^\.env($|\.)|\.dpapi$|^cookies?$|cookies?.*\.txt(\.json)?$|^(favorites|history|live-hub|appsettings)\.json$|\.pdb$|\.log$' -or
        $path -match '(?i)(^|/)(profiles?|[^/]*-(login|verify)-profile|backups|runtime|\.venv)(/|$)') {
        Add-Issue $path 'private data or diagnostic file'
    }
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    # Preserve required upstream copyright notices; their names and addresses are not user data.
    $upstream = $path -match '(^|/)(licenses/|fonts/LICENSE\.txt$)'
    $binary = $bytes.Length -gt 0 -and $text.IndexOf([char]0) -ge 0
    $views = @($text)
    if ($binary) { $views += [Text.Encoding]::Unicode.GetString($bytes) }
    foreach ($view in $views) {
        foreach ($rule in $patterns.Keys) {
            if ([regex]::IsMatch($view, $patterns[$rule])) { Add-Issue $path $rule }
        }
        if (-not $upstream -and -not $binary) {
            foreach ($match in [regex]::Matches($view, '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]+[^\s"''<>|;),]+')) {
                if ($match.Value -match '(?i)^[A-Z]:\\[sdw][*+?{]') { continue } # Regex quantifiers in log parsers.
                # Generic system/test paths are intentional fixtures, not personal workspace paths.
                if ($match.Value -notmatch '(?i)^[A-Z]:[\\/]+(?:Windows|Program Files(?: \(x86\))?|tmp|base)(?:[\\/]|$)') {
                    Add-Issue $path 'absolute workspace path'
                }
            }
        }
    }
    if ($leaf -eq 'manifest.json') {
        try { $manifest = $text.TrimStart([char]0xFEFF) | ConvertFrom-Json } catch { Add-Issue $path 'invalid manifest JSON'; return }
        foreach ($field in @('sourceRepo','sourceWorktree','os','toolchain','builtAtLocal')) {
            if ($manifest.PSObject.Properties[$field]) { Add-Issue $path ('host metadata field ' + $field) }
        }
        if ($manifest.tests -and $manifest.tests.PSObject.Properties['logPath']) { Add-Issue $path 'local diagnostic log reference' }
    }
}
function Test-Zip([IO.Stream]$stream, [string]$prefix, [int]$depth) {
    if ($depth -gt 3) { Add-Issue $prefix 'archive nesting limit'; return }
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $path = $prefix + $entry.FullName
            if ($entry.FullName -match '^[/\\]|\\|(^|/)\.\.(/|$)|:') { Add-Issue $path 'unsafe archive path'; continue }
            if ($entry.Length -gt 256MB) { Add-Issue $path 'entry exceeds inspection limit'; continue }
            $inputStream = $entry.Open(); $memory = [IO.MemoryStream]::new()
            try { $inputStream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $inputStream.Dispose(); $memory.Dispose() }
            Test-Content $bytes $path
            if ($entry.FullName -match '(?i)\.zip$') {
                $nested = [IO.MemoryStream]::new($bytes, $false)
                try { Test-Zip $nested ($path + '!/') ($depth + 1) } finally { $nested.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
}
if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Archive))
    try { Test-Zip $stream '' 0 } finally { $stream.Dispose() }
} else {
    $root = (Get-Item -LiteralPath $Directory -Force).FullName.TrimEnd('\','/')
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Inspection root cannot be a reparse point' }
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' })
    foreach ($file in $items) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { Add-Issue $relative 'reparse point'; continue }
        Test-Content ([IO.File]::ReadAllBytes($file.FullName)) $relative
    }
}
if ($issues.Count) {
    $issues | Sort-Object -Unique | ForEach-Object { Write-Output ('FAIL: ' + $_) }
    throw ('Public privacy preflight failed: ' + $issues.Count + ' candidates; values are not printed')
}
Write-Output ('PASS: public privacy preflight; inspected ' + $script:checkedFiles + ' files')
