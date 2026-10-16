param([Parameter(Mandatory)][string]$Workspace, [string]$Source = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Workspace) { throw 'Test workspace must be new' }
New-Item -ItemType Directory -Path $Workspace | Out-Null
$guard = Join-Path $Source 'tools\Test-PublishPrivacy.ps1'
function Invoke-Case([string]$name,[string]$file,[string]$content,[bool]$reject) {
    $directory = Join-Path $Workspace $name
    New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory $file),$content)
    $rejected = $false
    try { & $guard -Directory $directory | Out-Null } catch { $rejected = $true }
    if ($rejected -ne $reject) { throw ('Unexpected privacy decision: ' + $name) }
    Write-Output ('PASS: ' + $name)
}
Invoke-Case 'public-readme' 'README.md' 'Public documentation with relative paths.' $false
Invoke-Case 'host-manifest' 'manifest.json' '{"sourceRepo":"private source","os":{"version":"private build"}}' $true
Invoke-Case 'local-path' 'sample.txt' ('Z' + ':\private-example\source') $true
Invoke-Case 'credential' 'sample.txt' ('sk-' + ('a' * 30)) $true
Invoke-Case 'cookie-file' 'bili-cookies.txt' 'synthetic' $true
Invoke-Case 'private-log' 'build.log' 'synthetic' $true
Invoke-Case 'personal-history' 'history.json' '[]' $true
Invoke-Case 'json-key' 'config.json' ('{"apiKey":"' + ('q' * 30) + '"}') $true
Invoke-Case 'json-cookie' 'config.json' ('[{"name":"SESSDATA","value":"' + ('q' * 30) + '"}]') $true
Invoke-Case 'json-synthetic' 'config.json' '{"apiKey":"synthetic-placeholder"}' $false
Invoke-Case 'env-template' '.env.example' 'API_KEY=synthetic-placeholder' $false
$nestedRoot = Join-Path $Workspace 'nested-root'; New-Item -ItemType Directory -Path $nestedRoot | Out-Null
$inner = Join-Path $nestedRoot 'inner.zip'
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $Workspace 'local-path'),$inner)
$outer = Join-Path $Workspace 'nested.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($nestedRoot,$outer)
$rejected = $false
try { & $guard -Archive $outer | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Nested archive bypassed privacy preflight' }
Write-Output 'PASS: nested archive is inspected'
