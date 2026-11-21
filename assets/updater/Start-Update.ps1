param([ValidateSet('Auto','Check','Install')][string]$Mode='Check',[Parameter(Mandatory)][ValidateSet('cs','rust')][string]$Product)
$ErrorActionPreference='Stop'
$env:PSModulePath=(Join-Path $PSHOME 'Modules')+';'+$env:PSModulePath
. (Join-Path $PSScriptRoot 'Core.ps1')
. (Join-Path $PSScriptRoot 'Install.ps1')
$currentVersion=$env:JUBO_UPDATE_VERSION
if($currentVersion -notmatch '^\d{1,8}\.\d{1,8}\.\d{1,8}$'){throw '启动器版本无效'}
$cache=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("JuboLive/Updates/"+$Product)
[IO.Directory]::CreateDirectory($cache)|Out-Null
$catalogPath=Join-Path $cache 'channel.json';$disable=Join-Path $cache 'auto-off'
$ticket=$env:JUBO_UPDATE_TICKET;$ownerId=[int]$env:JUBO_UPDATE_OWNER
if($ticket -notmatch '^[a-f0-9]{32}$'){throw '更新会话无效'}
$requestDir=Safe-Child $cache ('requests/'+$ticket)
$cancelFile=Join-Path $requestDir 'cancel'
$installRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
$managedRust=$false
$exeName=$env:JUBO_UPDATE_EXE
if(($Product -eq 'cs' -and $exeName -notin @('看直播.exe','看直播-mpv.exe')) -or ($Product -eq 'rust' -and $exeName -ne 'live-hub-rust.exe')){throw '启动器文件名无效'}
if($Product -eq 'rust' -and (Split-Path (Split-Path $installRoot -Parent) -Leaf) -eq 'artifacts'){
    $junction=Join-Path (Split-Path $installRoot -Parent) 'current'
    $link=Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
    if($link -and $link.LinkType -eq 'Junction'){
        $target=[IO.Path]::GetFullPath([string]@($link.Target)[0]).TrimEnd('\')
        if(($target -eq $installRoot -or $junction -eq $installRoot) -and (Split-Path $target -Parent) -eq (Split-Path $junction -Parent) -and (Split-Path $target -Leaf) -match '^release-\d+\.\d+\.\d+(?:-[a-z0-9.-]+)?$'){
            $managedRust=$true;$installRoot=$target
        }
    }
}
$devLayout=$Product -eq 'cs' -and $exeName -eq '看直播-mpv.exe' -and !(Test-Path -LiteralPath (Join-Path $installRoot 'portable.marker'))
$variant='lite'
if($Product -eq 'cs' -and !$devLayout){
    $marker=Join-Path $installRoot 'portable.marker'
    if((Test-Path $marker) -and [IO.File]::ReadAllText($marker) -match 'full'){$variant='full'}
    elseif(!(Test-Path $marker) -and (Test-Path -LiteralPath (Join-Path $installRoot 'bin/mpv/mpv.exe'))){$variant='full'}
}
function Set-ProgressText([string]$text){Write-AtomicText (Join-Path $requestDir 'status.txt') $text}
function Check-Now {
    $r=Get-Catalog
    Write-AtomicText (Join-Path $requestDir 'version.txt') ([string]$r.version)
    if([version]$r.version -gt [version]$currentVersion){return "发现新版 $($r.version)。点击更新即可下载并安装。"}
    if([version]$r.version -lt [version]$currentVersion){return "当前版本 $currentVersion 高于渠道版本 $($r.version)。"}
    return "当前已是最新正式版 $currentVersion。"
}
if($Mode -eq 'Auto'){
    if(Test-Path $disable){return}
    $stamp=Join-Path $cache 'last-attempt'
    if((Test-Path $stamp) -and ([DateTime]::UtcNow-[IO.File]::GetLastWriteTimeUtc($stamp)).TotalHours -lt 12){return}
    Write-AtomicText $stamp ([DateTime]::UtcNow.ToString('o'))
    try{$null=Get-Catalog}catch{} # Manual check always retries and reports the error.
    return
}
[IO.Directory]::CreateDirectory($requestDir)|Out-Null
if($Mode -eq 'Check' -or $Mode -eq 'Install'){
    if($Mode -eq 'Install'){$script:ownedPayload=$null;$script:ownedDownload=$null}
    try{
        if($Mode -eq 'Install' -and !$env:JUBO_UPDATE_OWNER_START){
            $owner=Get-Process -Id $ownerId -ErrorAction Stop
            $env:JUBO_UPDATE_OWNER_START=$owner.StartTime.ToUniversalTime().Ticks.ToString()
        }
        if($Mode -eq 'Check'){Set-ProgressText (Check-Now)}else{Apply-Update}}
    catch{Set-ProgressText ('操作失败：'+$_.Exception.Message);exit 1}
    finally {
        foreach($tempPath in @($script:ownedPayload,$script:ownedDownload)){
            if(!$tempPath){continue}
            $full=[IO.Path]::GetFullPath($tempPath)
            if([IO.Path]::GetDirectoryName($full) -ne $requestDir){continue}
            try {
                if([IO.File]::Exists($full)){[IO.File]::Delete($full)}
                elseif([IO.Directory]::Exists($full) -and (Split-Path $full -Leaf) -match '^payload-[a-f0-9]{32}$' -and
                    !((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                    !(Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint)){
                    [IO.Directory]::Delete($full,$true)
                }
            }catch{Write-Verbose '本地暂存稍后可清理'}
        }
    }
    exit 0
}
