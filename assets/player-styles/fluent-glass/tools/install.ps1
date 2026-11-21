param(
    [string]$PlayerDirectory,
    [string]$PythonExecutable,
    [switch]$PrepareOnly,
    [switch]$ForceBuild,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PYTHONUTF8 = '1'
. (Join-Path $PSScriptRoot 'install_helpers.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $projectRoot '.runtime'
$configFile = Join-Path $runtimeDirectory 'install.json'
$saved = Read-InstallConfig $configFile
$player = Find-PlayerDirectory $PlayerDirectory ($PSBoundParameters.ContainsKey('PlayerDirectory')) $saved
$python = Join-Path $projectRoot '.venv\Scripts\python.exe'
$knownSkin = Join-Path $player 'Skins/FluentGlass.dsf'
$useInstalled = (Test-Path -LiteralPath $knownSkin -PathType Leaf) -and
    (Get-Sha256File $knownSkin) -eq '9e454fe3b34b239b640dcb276e0bc71a3b510109a1f0d6f8af986517caea8cf3'
if ($useInstalled) {
    $skinFile = $knownSkin
    $buildRequired = $false
} else {
    $basePython = Find-Python $python $PythonExecutable ($PSBoundParameters.ContainsKey('PythonExecutable'))
    if (-not (Test-Path -LiteralPath $python)) {
        Write-Host '首次运行：正在创建本目录的 Python 环境……'
        $arguments = @($basePython.arguments) + @('-m','venv',(Join-Path $projectRoot '.venv'))
        Invoke-PythonStep $basePython.executable $arguments '创建 Python 环境失败；请检查 Python 是否包含 venv/ensurepip。'
    }
    $requirements = Join-Path $projectRoot 'requirements.txt'
    $requirementsHash = Get-Sha256File $requirements
    $requirementsChanged = $saved -and $saved.requirementsHash -and $saved.requirementsHash -ne $requirementsHash
    if ($requirementsChanged -or -not (Test-Dependencies $python $requirements)) {
        Write-Host '正在安装构建依赖（首次需要联网）……'
        $pipReady = $false
        try { $null = & $python -m pip --version 2>$null; $pipReady = $LASTEXITCODE -eq 0 } catch { }
        if (-not $pipReady) { Invoke-PythonStep $python @('-m','ensurepip','--upgrade') '无法准备 pip，请检查 Python 安装。' }
        Invoke-PythonStep $python @('-m','pip','--disable-pip-version-check','install','-r',$requirements) '依赖安装失败；请检查网络后重新运行安装，已有设置会保留。'
        if (-not (Test-Dependencies $python $requirements)) { throw '构建依赖仍不可用，请检查 requirements.txt 与 Python 环境。' }
    }
    $defaultSkin = Join-Path $player 'Skins\Default.dsf'
    $fingerprint = Get-ProjectFingerprint $projectRoot $defaultSkin
    $skinFile = Join-Path $projectRoot 'dist\FluentGlass.dsf'
    $buildRequired = $ForceBuild -or -not (Test-Path -LiteralPath $skinFile) -or -not $saved -or $saved.buildFingerprint -ne $fingerprint
    if (-not $buildRequired -and $saved.skinHash) {
        $buildRequired = (Get-Sha256File $skinFile) -ne $saved.skinHash
    }
    if ($buildRequired) {
        Write-Host '正在使用本机 Default.dsf 构建皮肤；默认资源仅保留在本机……'
        Invoke-PythonStep $python @((Join-Path $PSScriptRoot 'build.py'),'--default',$defaultSkin) '皮肤构建失败；未修改原播放器，请检查上述错误。'
    }
}
New-Item -ItemType Directory -Path $runtimeDirectory,(Join-Path $runtimeDirectory 'Skins'),(Join-Path $runtimeDirectory 'Language') -Force | Out-Null
$previewExe = Join-Path $runtimeDirectory 'PotPlayerMini64.exe'
$running = Get-Process -Name PotPlayerMini64 -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $previewExe } catch { $false }
} | Select-Object -First 1
$restartRequired = $false
$playerFiles = @((Get-Item -LiteralPath (Join-Path $player 'PotPlayerMini64.exe')))
$playerFiles += @(Get-ChildItem -LiteralPath $player -Filter '*.dll' -File)
foreach ($source in $playerFiles) {
    $destination = Join-Path $runtimeDirectory $source.Name
    $different = -not (Test-Path -LiteralPath $destination)
    if (-not $different) { $different = (Get-Sha256File $source.FullName) -ne (Get-Sha256File $destination) }
    if ($different) {
        if ($running) { $restartRequired = $true }
        else { Copy-Item -LiteralPath $source.FullName -Destination $destination }
    }
}
$skinHash = Get-Sha256File $skinFile
$runtimeSkin = Join-Path $runtimeDirectory 'Skins\FluentGlass.dsf'
$skinChanged = -not (Test-Path -LiteralPath $runtimeSkin)
if (-not $skinChanged) { $skinChanged = (Get-Sha256File $runtimeSkin) -ne $skinHash }
if ($skinChanged) {
    if ($running) { $restartRequired = $true }
    else { Copy-Item -LiteralPath $skinFile -Destination $runtimeSkin }
}
$language = Join-Path $player 'Language\Chinese(Simplified).ini'
if (-not $running -and (Test-Path -LiteralPath $language)) { Copy-Item -LiteralPath $language -Destination (Join-Path $runtimeDirectory 'Language') }
$ini = Join-Path $runtimeDirectory 'PotPlayerMini64.ini'
if (-not (Test-Path -LiteralPath $ini)) {
@'
[Settings]
LastSkinName=FluentGlass.dsf
LastSkinXmlName=VideoSkin.xml
LastSkinXmlNameVideo=VideoSkin.xml
LastSkinXmlNameAudio=AudioSkin.xml
LanguageIni=Chinese(Simplified).ini
CheckAutoUpdate=0
AutoUpdateStart=0
AutoHideSkin=0
SkinUseOsc=0
RememberPosition=1
StartCenterPos=1
MultipleInstance=1
PlayListWindowVisible=0
[Positions]
MainWidth2=1120
MainHeight2=725
'@ | Set-Content -LiteralPath $ini -Encoding Unicode
}
$config = [pscustomobject]@{
    schemaVersion=1
    playerDirectory=$player
    pythonExecutable=$python
    playerExe=$previewExe
    skinFile=$skinFile
    runtimeDirectory=$runtimeDirectory
    buildFingerprint=$fingerprint
    requirementsHash=$requirementsHash
    skinHash=$skinHash
    restartRequired=[bool]$restartRequired
    buildPerformed=[bool]$buildRequired
}
$serialized = $config | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText($configFile,$serialized,(New-Object Text.UTF8Encoding($false)))
if ($restartRequired) { Write-Warning '新版本已构建；请先关闭本项目预览播放器，再重新运行。运行中的文件未被覆盖。' }
elseif (-not $Json) { Write-Host '本地安装完成。双击预览皮肤.cmd 启动；原播放器安装目录未被修改。' }
if ($Json) { $config | ConvertTo-Json -Depth 3 -Compress }
