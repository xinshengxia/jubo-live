# Shared local setup helpers. This script never changes an installed player.
$script:RequiredPlayerFiles = @('PotPlayerMini64.exe','PotPlayer64.dll','Skins\Default.dsf')

function Test-PlayerDirectory([string]$Directory) {
    if (-not $Directory -or -not (Test-Path -LiteralPath $Directory -PathType Container)) { return $false }
    foreach ($name in $script:RequiredPlayerFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { return $false }
    }
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead((Join-Path $Directory 'PotPlayerMini64.exe'))
        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { return $false }
    } catch { return $false }
    finally { if ($stream) { $stream.Dispose() } }
    return $true
}

function Read-InstallConfig([string]$ConfigFile) {
    if (Test-Path -LiteralPath $ConfigFile) {
        try { return Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { Write-Warning '本地安装记录无法读取，将重新检测 PotPlayer。' }
    }
    return $null
}

function Get-Sha256File([string]$File) {
    $stream = [IO.File]::OpenRead($File)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

function Find-PlayerDirectory([string]$ExplicitDirectory,[bool]$HasExplicitDirectory,$SavedConfig) {
    if ($HasExplicitDirectory) {
        if (-not (Test-PlayerDirectory $ExplicitDirectory)) {
            throw '指定目录不是完整的 PotPlayer x64 安装。请用 -PlayerDirectory 指定包含 PotPlayerMini64.exe、PotPlayer64.dll、Skins\Default.dsf 的目录。'
        }
        return (Resolve-Path -LiteralPath $ExplicitDirectory).Path
    }
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if ($SavedConfig -and $SavedConfig.playerDirectory) { $candidates.Add([string]$SavedConfig.playerDirectory) }
    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe')) {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($entry) {
            if ($entry.'(default)') { $candidates.Add((Split-Path -Parent ([string]$entry.'(default)').Trim('"'))) }
            if ($entry.Path) { $candidates.Add([string]$entry.Path) }
        }
    }
    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        foreach ($entry in @(Get-ItemProperty -Path $key -ErrorAction SilentlyContinue)) {
            if ($entry.DisplayName -match 'PotPlayer') {
                if ($entry.InstallLocation) { $candidates.Add([string]$entry.InstallLocation) }
                if ($entry.DisplayIcon) {
                    $iconPath = (([string]$entry.DisplayIcon) -replace ',\s*-?\d+$','').Trim('"')
                    $candidates.Add((Split-Path -Parent $iconPath))
                }
                if ($entry.UninstallString) {
                    $uninstall = [string]$entry.UninstallString
                    if ($uninstall -match '^\s*"([^"]+)"') { $candidates.Add((Split-Path -Parent $Matches[1])) }
                    elseif ($uninstall -match '^\s*(.*?\.exe)(?:\s|$)') { $candidates.Add((Split-Path -Parent $Matches[1])) }
                }
            }
        }
    }
    $command = Get-Command PotPlayerMini64.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $candidates.Add((Split-Path -Parent $command.Source)) }
    foreach ($process in @(Get-Process -Name PotPlayerMini64 -ErrorAction SilentlyContinue)) {
        try { $candidates.Add((Split-Path -Parent $process.Path)) } catch { }
    }
    foreach ($base in @($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA)) {
        if ($base) {
            foreach ($relative in @('DAUM\PotPlayer','PotPlayer','Programs\PotPlayer','Programs\DAUM\PotPlayer')) {
                $candidates.Add((Join-Path $base $relative))
            }
        }
    }
    foreach ($candidate in $candidates) {
        if (Test-PlayerDirectory $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw '未找到 PotPlayer x64。请先安装播放器，再运行：.\安装皮肤.cmd -PlayerDirectory "实际安装目录"。目录中需有 PotPlayerMini64.exe、PotPlayer64.dll 和 Skins\Default.dsf。'
}

function Test-CompatiblePython([string]$Executable,[string[]]$PrefixArguments = @()) {
    try {
        $null = & $Executable @PrefixArguments -c "import sys,struct; raise SystemExit(0 if sys.version_info >= (3,10) and struct.calcsize('P') == 8 else 1)" 2>$null
        return $LASTEXITCODE -eq 0
    } catch { return $false }
}

function Find-Python([string]$ProjectPython,[string]$ExplicitPython,[bool]$HasExplicitPython) {
    if ($HasExplicitPython -and (-not $ExplicitPython -or -not (Test-CompatiblePython $ExplicitPython))) {
        throw '指定的 -PythonExecutable 无法运行 64 位 Python 3.10 或更高版本。'
    }
    if (Test-Path -LiteralPath $ProjectPython) {
        if (-not (Test-CompatiblePython $ProjectPython)) {
            throw '本目录 .venv 不是可用的 64 位 Python 3.10+ 环境。请将 .venv 改名备份后重新运行安装。'
        }
        return [pscustomobject]@{ executable=$ProjectPython; arguments=@() }
    }
    if ($HasExplicitPython) {
        return [pscustomobject]@{ executable=$ExplicitPython; arguments=@() }
    }
    foreach ($name in @('py.exe','python.exe','python3.exe')) {
        foreach ($command in @(Get-Command $name -All -ErrorAction SilentlyContinue)) {
            # WindowsApps aliases can open the Store instead of executing Python.
            if ($command.Source -match '\\WindowsApps\\') { continue }
            $prefix = @()
            if ($name -eq 'py.exe') { $prefix = @('-3-64') }
            if (Test-CompatiblePython $command.Source $prefix) {
                return [pscustomobject]@{ executable=$command.Source; arguments=$prefix }
            }
        }
    }
    throw '需要 64 位 Python 3.10 或更高版本。请先安装 Python（启用 PATH），或用 -PythonExecutable 指定 python.exe，然后重新运行安装皮肤.cmd。'
}

function Get-ProjectFingerprint([string]$ProjectRoot,[string]$DefaultSkin) {
    $paths = @((Join-Path $ProjectRoot 'tools\build.py'),(Join-Path $ProjectRoot 'tools\secondary_skins.py'),(Join-Path $ProjectRoot 'requirements.txt'))
    foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md')) {
        $path = Join-Path $ProjectRoot $name
        if (Test-Path -LiteralPath $path) { $paths += $path }
    }
    $paths += @(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'assets') -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })
    $parts = @()
    foreach ($path in $paths) {
        $relative = $path.Substring($ProjectRoot.Length).Replace('\','/')
        $parts += $relative + ':' + (Get-Sha256File $path)
    }
    $parts += 'Default.dsf:' + (Get-Sha256File $DefaultSkin)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))))).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Test-Dependencies([string]$Python,[string]$RequirementsFile) {
    try { $null = & $Python (Join-Path $PSScriptRoot 'check_dependencies.py') $RequirementsFile 2>$null; return $LASTEXITCODE -eq 0 }
    catch { return $false }
}

function Invoke-PythonStep([string]$Python,[string[]]$Arguments,[string]$FailureMessage) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Python @Arguments 2>&1 | ForEach-Object {
            $message = [string]$_
            if ($_ -is [Management.Automation.ErrorRecord]) { $message = $_.Exception.Message }
            if (-not [string]::IsNullOrWhiteSpace($message) -and $message -ne 'System.Management.Automation.RemoteException') { Write-Host $message }
        }
        $stepExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    if ($stepExitCode -ne 0) { throw $FailureMessage }
}
