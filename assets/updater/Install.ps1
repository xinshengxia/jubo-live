function Replace-FromFile([string]$source,[string]$target) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
    $temp=$target+'.'+[guid]::NewGuid().ToString('N')+'.update-tmp'
    try {
        [IO.File]::Copy($source,$temp)
        if([IO.File]::Exists($target)){[IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)}
        else {[IO.File]::Move($temp,$target)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Install-InPlace([string]$payload) {
    # Keep the archive, per-file backups and a journal separate from application data.
    $backupParent=if($devLayout){Safe-Child $installRoot 'artifacts/updater'}else{Safe-Child $installRoot '.jubo-updates'}
    $pending=Join-Path $backupParent 'pending.txt'
    function Restore-Plan([string]$dir){
        $journal=Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $dir 'plan.json')|ConvertFrom-Json
        foreach($item in $journal){
            $rel=[string]$item.rel
            if(!(Is-Managed $rel) -and !($Product -eq 'cs' -and $rel -in @($exeName,($exeName+'.config'))) -and !($devLayout -and $rel -in @('看直播-mpv.exe','看直播-mpv.exe.config','live-title.lua','live-resync.lua'))){throw '回退清单路径无效'}
            $target=Safe-Child $installRoot $rel
            $actual=if([IO.File]::Exists($target)){(Get-FileHash -LiteralPath $target).Hash}else{''}
            if($actual -eq $item.old){continue}
            if($actual -ne $item.next){throw '安装期间有文件被其他程序修改，已保留回退副本'}
            if($item.old){
                $copy=Safe-Child (Join-Path $dir 'files') $rel
                if((Get-FileHash -LiteralPath $copy).Hash -ne $item.old){throw '回退文件校验失败'}
                Replace-FromFile $copy $target
            }else{[IO.File]::Delete($target)}
        }
        [IO.File]::Delete($pending)
    }
    if([IO.File]::Exists($pending)){
        $id=[IO.File]::ReadAllText($pending).Trim()
        if($id -notmatch '^[a-f0-9]{32}$'){throw '回退记录无效'}
        Restore-Plan (Safe-Child $backupParent $id)
    }
    $id=[guid]::NewGuid().ToString('N');$backup=Safe-Child $backupParent $id
    $oldMap=@{}
    if(!$devLayout -and (Test-Path -LiteralPath "$installRoot/manifest.json")){
        $old=Get-Content -Raw -Encoding utf8 -LiteralPath "$installRoot/manifest.json"|ConvertFrom-Json
        $rows=if($Product -eq 'cs'){$old.artifacts}else{$old.files}
        foreach($row in $rows){$oldMap[[string]$row.path]=[string]$row.sha256}
    }
    $files=@{}
    foreach($f in Get-ChildItem -LiteralPath $payload -File -Recurse){
        $rel=$f.FullName.Substring($payload.Length+1).Replace('\','/')
        if($devLayout){
            if($rel -in @('manifest.json','deps.lock.json','portable.marker','使用说明.md','LICENSE','THIRD_PARTY_NOTICES.md') -or $rel.StartsWith('bin/')){continue}
        }
        if($Product -eq 'cs' -and $rel -eq '看直播.exe'){$rel=$exeName}
        elseif($Product -eq 'cs' -and $rel -eq '看直播.exe.config'){$rel=$exeName+'.config'}
        $files[$rel]=$f.FullName
    }
    if($devLayout){
        foreach($lua in 'live-title.lua','live-resync.lua'){
            $rootLua=Join-Path $installRoot $lua;$mirror=Join-Path $installRoot "lua/$lua"
            if((Test-Path $rootLua) -and (Test-Path $mirror) -and (Get-FileHash $rootLua).Hash -eq (Get-FileHash $mirror).Hash){$files[$lua]=$files["lua/$lua"]}
        }
    }
    $plan=@(foreach($key in (@($files.Keys)+@($oldMap.Keys)|Sort-Object -Unique)){
        if(!$files.ContainsKey($key) -and !(Is-Managed $key)){continue}
        $target=Safe-Child $installRoot $key
        $before=if([IO.File]::Exists($target)){(Get-FileHash -LiteralPath $target).Hash}else{''}
        $after=if($files.ContainsKey($key)){(Get-FileHash -LiteralPath $files[$key]).Hash}else{''}
        if($before -eq $after){continue}
        # Preserve customised player configuration and any independently changed obsolete file.
        if($before -and ((!$after -and $oldMap[$key] -ne $before) -or
            ($key -match '^bin/mpv/portable_config/' -and $oldMap[$key] -ne $before))){continue}
        if($before){
            $save=Safe-Child (Join-Path $backup 'files') $key
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($save))|Out-Null
            [IO.File]::Copy($target,$save)
            if((Get-FileHash -LiteralPath $save).Hash -ne $before){throw '备份校验失败'}
        }
        [pscustomobject]@{rel=$key;old=$before;next=$after}
    })
    Write-AtomicText (Join-Path $backup 'plan.json') (ConvertTo-Json -InputObject $plan -Compress)
    Write-AtomicText $pending $id
    try {
        foreach($item in $plan){
            $target=Safe-Child $installRoot $item.rel
            $currentHash=if([IO.File]::Exists($target)){(Get-FileHash -LiteralPath $target).Hash}else{''}
            if($currentHash -ne $item.old){throw '程序文件已由其他进程更改'}
            if($item.next){Replace-FromFile $files[$item.rel] $target;if((Get-FileHash -LiteralPath $target).Hash -ne $item.next){throw '安装后回读失败'}}
            else{[IO.File]::Delete($target)}
        }
        [IO.File]::Delete($pending)
        Write-AtomicText (Join-Path $backupParent 'previous.txt') $id
    }catch{Restore-Plan $backup;throw}
}
function Install-ManagedRust([string]$payload,[string]$version){
    $parent=Split-Path -Parent $installRoot;$link=Join-Path $parent 'current'
    $current=Get-Item -LiteralPath $link -Force
    if($current.LinkType -ne 'Junction' -or [IO.Path]::GetFullPath([string]@($current.Target)[0]) -ne $installRoot){throw '当前版本已变化，请重新检查'}
    $dest=Safe-Child $parent ("release-"+$version)
    $next=Safe-Child $parent ('update-next-'+$ticket);$prior=Safe-Child $parent ('update-prior-'+$ticket)
    $fingerprint=(Get-FileHash -LiteralPath (Join-Path $payload 'manifest.json')).Hash
    $marker=Join-Path $dest '.jubo-incomplete'
    if(![IO.Directory]::Exists($dest)){
        [IO.Directory]::CreateDirectory($dest)|Out-Null
        Write-AtomicText $marker $fingerprint
    }
    if([IO.File]::Exists($marker)){
        if([IO.File]::ReadAllText($marker) -ne $fingerprint){throw '目标版本已有不同的未完成安装'}
        foreach($f in Get-ChildItem -LiteralPath $payload -File -Recurse){
            $rel=$f.FullName.Substring($payload.Length+1).Replace('\','/');$to=Safe-Child $dest $rel
            if(![IO.File]::Exists($to) -or (Get-FileHash -LiteralPath $to).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash){
                Replace-FromFile $f.FullName $to
            }
        }
        [IO.File]::Delete($marker)
    }
    if(@(Get-ChildItem -LiteralPath $dest -Recurse -Force -Attributes ReparsePoint).Count -or
        @(Get-ChildItem -LiteralPath $payload -File -Recurse).Count -ne @(Get-ChildItem -LiteralPath $dest -File -Recurse -Force).Count){throw '目标版本目录含额外文件或链接'}
    foreach($f in Get-ChildItem -LiteralPath $payload -File -Recurse){
        $rel=$f.FullName.Substring($payload.Length+1).Replace('\','/')
        if((Get-FileHash -LiteralPath (Safe-Child $dest $rel)).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash){throw '新版本目录回读失败'}
    }
    New-Item -ItemType Junction -Path $next -Target $dest -ErrorAction Stop|Out-Null
    Rename-Item -LiteralPath $link -NewName (Split-Path $prior -Leaf)
    try {Rename-Item -LiteralPath $next -NewName 'current'}catch{Rename-Item -LiteralPath $prior -NewName 'current';throw}
    $saved=Get-Item -LiteralPath $prior -Force
    if($saved.LinkType -eq 'Junction' -and [string]@($saved.Target)[0] -eq $installRoot){[IO.Directory]::Delete($prior,$false)}
}
function Quote-Argument([string]$word){
    return '"'+([regex]::Replace(([regex]::Replace($word,'(\\*)"','$1$1\"')),'(\\+)$','$1$1'))+'"'
}
function Restart-Owner {
    $exe=if($managedRust){Join-Path (Join-Path (Split-Path $installRoot -Parent) 'current') $exeName}else{Join-Path $installRoot $exeName}
    $argsJson=$env:JUBO_UPDATE_ARGS
    $list=if($argsJson){@($argsJson|ConvertFrom-Json)}else{@()}
    $argString=(@(foreach($word in $list){if($null -ne $word){Quote-Argument ([string]$word)}}) -join ' ')
    $si=[Diagnostics.ProcessStartInfo]::new($exe,$argString);$si.WorkingDirectory=Split-Path -Parent $exe;$si.UseShellExecute=$false
    [Diagnostics.Process]::Start($si).Dispose()
}
function Apply-Update {
    $record=if($env:JUBO_UPDATE_OFFLINE_CATALOG){Read-SignedCatalog ([IO.File]::ReadAllText($env:JUBO_UPDATE_OFFLINE_CATALOG))}else{Get-Catalog}
    if([version]$record.version -le [version]$currentVersion){throw '当前版本已经是此清单中的最新版本'}
    $prefix=if($Product -eq 'cs'){'jubo-live'}else{'jubo-live-rust'}
    $name="$prefix-$($record.version)-win-x64-$variant.zip"
    $asset=@($record.assets|Where-Object name -ceq $name)[0]
    $download=Join-Path $requestDir $name
    $payload=Join-Path $requestDir ('payload-'+[guid]::NewGuid().ToString('N'))
    $script:ownedPayload=$payload
    if(!$env:JUBO_UPDATE_OFFLINE_ZIP){$script:ownedDownload=$download}
    Set-ProgressText '正在下载并校验更新包…'
    if($env:JUBO_UPDATE_OFFLINE_ZIP){$download=$env:JUBO_UPDATE_OFFLINE_ZIP}else{Get-Package $asset $record.version $download}
    Set-ProgressText '正在核对安装包与文件清单…'
    $null=Expand-VerifiedPackage $download $payload $asset $record.version
    $mutex=[Threading.Mutex]::new($false,('Local\JuboLive.Install.'+$Product))
    $held=$false;$restart=$false
    try{
        try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true}
        if(!$held){throw '已有更新正在安装'}
        $owner=Get-Process -Id $ownerId -ErrorAction Stop
        if($owner.StartTime.ToUniversalTime().Ticks -ne [long]$env:JUBO_UPDATE_OWNER_START){throw '启动器身份已变化'}
        Set-ProgressText '已校验。等待观看会话结束，随后自动重启并替换程序…'
        Write-AtomicText (Join-Path $requestDir 'ready') $record.version
        $until=[DateTime]::UtcNow.AddHours(12)
        while(!$owner.WaitForExit(400)){
            if([IO.File]::Exists($cancelFile) -or [DateTime]::UtcNow -gt $until){throw '更新已取消或等待超时'}
        }
        if(![IO.File]::Exists((Join-Path $requestDir 'idle-ok'))){throw '启动器未确认会话已结束，已保留旧版。请重开后再更新'}
        $restart=$true
        if([IO.File]::Exists($cancelFile)){throw '已取消更新'}
        # A second launcher, relay or bundled player may still hold program files.
        $rootPrefix=$installRoot.TrimEnd('\')+'\'
        $linkPrefix=if($managedRust){(Join-Path (Split-Path $installRoot -Parent) 'current')+'\'}else{$rootPrefix}
        $processNames=@([IO.Path]::GetFileNameWithoutExtension($exeName))
        if($Product -eq 'cs' -and $variant -eq 'full'){$processNames+='mpv'}
        while(@(Get-Process -Name $processNames -ErrorAction SilentlyContinue|Where-Object {
            $_.Path -and ($_.Path.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase) -or $_.Path.StartsWith($linkPrefix,[StringComparison]::OrdinalIgnoreCase))
        }).Count){
            Set-ProgressText '另一个程序会话仍在使用安装目录，正在等待它正常退出…'
            if([DateTime]::UtcNow -gt $until -or [IO.File]::Exists($cancelFile)){throw '等待其他会话超时或已取消'}
            Start-Sleep -Seconds 2
        }
        Set-ProgressText '正在替换程序文件…'
        if($managedRust){Install-ManagedRust $payload $record.version}else{Install-InPlace $payload}
        Set-ProgressText ("已安装 "+$record.version+"，正在重新打开程序。")
    }finally{
        if([IO.File]::Exists((Join-Path $requestDir 'ready'))){[IO.File]::Delete((Join-Path $requestDir 'ready'))}
        if($held){$mutex.ReleaseMutex()};$mutex.Dispose()
        if($restart){Restart-Owner}
    }
}
