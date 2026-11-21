param([ValidateSet('Auto','Dialog','Check','Install')][string]$Mode='Dialog',[Parameter(Mandatory)][ValidateSet('cs','rust')][string]$Product)
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
    try{if($Mode -eq 'Check'){Set-ProgressText (Check-Now)}else{Apply-Update}}
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
if($Mode -eq 'Dialog'){
    $owner=Get-Process -Id $ownerId -ErrorAction Stop
    $env:JUBO_UPDATE_OWNER_START=$owner.StartTime.ToUniversalTime().Ticks.ToString()
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$form=[Windows.Forms.Form]::new();$form.Text='看直播 · 检查更新';$form.StartPosition='CenterScreen';$form.AutoScaleMode='Dpi';$form.ClientSize=[Drawing.Size]::new(492,228)
$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false
$form.Font=[Drawing.Font]::new('Microsoft YaHei UI',9)
$who=if($Product -eq 'cs'){'C#'}else{'Rust'}
$heading=[Windows.Forms.Label]::new();$heading.Text="看直播 $who · 当前版本 $currentVersion";$heading.SetBounds(18,16,455,27);$form.Controls.Add($heading)
$auto=[Windows.Forms.CheckBox]::new();$auto.Text='自动检查正式版更新';$auto.Checked=!(Test-Path $disable);$auto.SetBounds(18,48,440,25);$form.Controls.Add($auto)
$auto.Add_CheckedChanged({try{if($auto.Checked){if(Test-Path $disable){[IO.File]::Delete($disable)}}else{Write-AtomicText $disable 'off'}}catch{$status.Text='无法保存自动检查设置。'}})
$status=[Windows.Forms.Label]::new();$status.SetBounds(18,84,455,78);$status.Text='准备检查更新…';$form.Controls.Add($status)
function Add-Button([string]$label,[int]$x,[int]$width){
    $b=[Windows.Forms.Button]::new();$b.Text=$label;$b.SetBounds($x,173,$width,34);$form.Controls.Add($b);return $b
}
$check=Add-Button '检查更新' 18 100;$install=Add-Button '更新' 127 85;$offline=Add-Button '从本地安装…' 221 128
$done=Add-Button '关闭' 365 100;$install.Enabled=$false
$script:work=$null;$script:action='';$script:finished=$false
function Start-Worker([string]$action,[string]$zip='',[string]$signed=''){
    if($script:work -and !$script:work.HasExited){
        if($script:action -eq 'Check' -and $action -eq 'Install' -and $zip){$script:work.Kill();$script:work.WaitForExit();$script:work.Dispose();$script:work=$null}
        else{return}
    }
    foreach($n in 'ready','cancel','idle-ok','status.txt'){ $file=Join-Path $requestDir $n;if(Test-Path -LiteralPath $file){[IO.File]::Delete($file)} }
    $si=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
    $si.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '+(Quote-Argument $script:ownScript)+' -Mode '+$action+' -Product '+$Product
    $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.EnvironmentVariables['JUBO_UPDATE_OFFLINE_ZIP']=$zip;$si.EnvironmentVariables['JUBO_UPDATE_OFFLINE_CATALOG']=$signed
    $script:work=[Diagnostics.Process]::Start($si);$script:action=$action
    $check.Enabled=$install.Enabled=$false;$offline.Enabled=($action -eq 'Check')
    $status.Text=if($action -eq 'Install'){'正在准备更新…'}else{'正在检查，直连失败会尝试备用线路…'}
}
$script:ownScript=$PSCommandPath
$check.Add_Click({Start-Worker 'Check'})
$install.Add_Click({Start-Worker 'Install'})
$offline.Add_Click({
    $pick=[Windows.Forms.OpenFileDialog]::new()
    try{
        $pick.Title='选择本版本的正式 ZIP 包';$pick.Filter='ZIP 文件|*.zip';if($pick.ShowDialog($form) -ne 'OK'){return};$zip=$pick.FileName
        $pick.Title='选择与 ZIP 配套的签名清单 channel.json';$pick.FileName='channel.json';$pick.Filter='签名清单|*.json';if($pick.ShowDialog($form) -ne 'OK'){return}
        Start-Worker 'Install' $zip $pick.FileName
    }finally{$pick.Dispose()}
})
$done.Add_Click({$form.Close()})
$form.Add_FormClosing({param($sender,$closingArgs)
    if($script:work -and !$script:work.HasExited -and $script:action -eq 'Install'){
        if(Test-Path (Join-Path $requestDir 'idle-ok')){$closingArgs.Cancel=$true;return}
        Write-AtomicText $cancelFile 'cancel'
    }
})
$timer=[Windows.Forms.Timer]::new();$timer.Interval=500
$timer.Add_Tick({
    if(!$script:work){return}
    $file=Join-Path $requestDir 'status.txt'
    if([IO.File]::Exists($file)){try{$status.Text=[IO.File]::ReadAllText($file)}catch [IO.IOException]{}}
    if($script:work.HasExited){
        $success=$script:work.ExitCode -eq 0;$script:work.Dispose();$script:work=$null
        $check.Enabled=$offline.Enabled=$true;$install.Enabled=$false
        if($script:action -eq 'Install' -and $success){$form.Close();return}
        try{if(Test-Path $catalogPath){$record=Read-SignedCatalog ([IO.File]::ReadAllText($catalogPath));$install.Enabled=([version]$record.version -gt [version]$currentVersion)}}catch{$install.Enabled=$false}
    }
})
$form.Add_Shown({$timer.Start();Start-Worker 'Check'})
try{[void]$form.ShowDialog()}finally{$timer.Dispose();$form.Dispose()}
