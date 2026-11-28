# Shared by the C# and Rust Windows launchers. Windows PowerShell 5.1+.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
function Write-AtomicText([string]$path,[string]$value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
    $temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temp,$value,[Text.UTF8Encoding]::new($false))
        if([IO.File]::Exists($path)){[IO.File]::Replace($temp,$path,[System.Management.Automation.Language.NullString]::Value)}
        else {try{[IO.File]::Move($temp,$path)}catch [IO.IOException]{if(![IO.File]::Exists($path)){throw};[IO.File]::Replace($temp,$path,[System.Management.Automation.Language.NullString]::Value)}}
    } finally {if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Read-SignedCatalog([string]$text) {
    if($text.Length -gt 65536){throw '签名清单过大'}
    $box=$text|ConvertFrom-Json
    $bytes=[Convert]::FromBase64String($box.payload);$sig=[Convert]::FromBase64String($box.signature)
    $rsa=[Security.Cryptography.RSACryptoServiceProvider]::new([Security.Cryptography.CspParameters]::new(24));$rsa.PersistKeyInCsp=$false
    try {
        $rsa.FromXmlString([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'channel-key.xml')))
        if(!$rsa.VerifyData($bytes,'SHA256',$sig)){throw '更新清单的发布者签名无效'}
    } finally {$rsa.Dispose()}
    $record=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
    if($record.schema -ne 1 -or $record.product -cne $Product -or $record.version -notmatch '^\d{1,8}\.\d{1,8}\.\d{1,8}$'){throw '清单不属于此程序或不是正式版本'}
    $ver=[version]$record.version
    $expected=if($Product -eq 'cs'){@("jubo-live-$ver-win-x64-full.zip","jubo-live-$ver-win-x64-lite.zip")}else{@("jubo-live-rust-$ver-win-x64-lite.zip")}
    if(@($record.assets).Count -ne @($expected).Count){throw '更新清单附件不完整'}
    foreach($n in $expected){
        $a=@($record.assets|Where-Object name -ceq $n)
        if($a.Count -ne 1 -or $a[0].sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$a[0].size -le 0 -or [long]$a[0].size -gt 512MB){throw '更新附件信息无效'}
    }
    return $record
}
function Write-DownloadProgress([string]$path,[long]$downloaded,[long]$total) {
    try {
        $snapshot = [ordered]@{downloaded=$downloaded; total=$total} | ConvertTo-Json -Compress
        Write-AtomicText $path $snapshot
    } catch [IO.IOException] {
        # A reader may briefly hold the previous snapshot on Windows.
        Write-Verbose 'Download progress snapshot unavailable'
    }
}
function Receive-PublicFile([uri]$uri,[string]$path,[long]$limit,[int]$seconds,[string]$progressPath) {
    $hosts=@('github.com','api.github.com','raw.githubusercontent.com','release-assets.githubusercontent.com','objects.githubusercontent.com','cdn.jsdelivr.net','gcore.jsdelivr.net','fastly.jsdelivr.net','gh-proxy.com','gh-proxy.org','ghfast.top')
    $watch=[Diagnostics.Stopwatch]::StartNew()
    for($i=0;$i -lt 7;$i++){
        if($uri.Scheme -ne 'https' -or $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Host -notin $hosts){throw '更新来源跳转至未允许的站点'}
        $req=[Net.HttpWebRequest]::Create($uri);$req.AllowAutoRedirect=$false;$req.UserAgent='JuboLive-Update/1';$req.UseDefaultCredentials=$false
        $req.Timeout=1000*[Math]::Min(12,$seconds);$req.ReadWriteTimeout=12000
        $resp=$req.GetResponse()
        try {
            $code=[int]$resp.StatusCode
            if($code -in 301,302,303,307,308){$uri=[uri]::new($uri,$resp.Headers['Location']);continue}
            if($code -ne 200 -or $resp.ContentLength -gt $limit){throw '更新来源响应无效'}
            $inputStream=$resp.GetResponseStream()
            $file=[IO.File]::Open($path,'Create','Write','None')
            try {
                $buf=New-Object byte[] 65536
                $reportedAt=$watch.ElapsedMilliseconds
                while(($n=$inputStream.Read($buf,0,$buf.Length)) -gt 0){
                    if($file.Length+$n -gt $limit -or $watch.Elapsed.TotalSeconds -gt $seconds){throw '下载超出大小或时间限制'}
                    if($script:cancelFile -and [IO.File]::Exists($script:cancelFile)){throw '已取消更新'}
                    $file.Write($buf,0,$n)
                    if($progressPath -and ($watch.ElapsedMilliseconds-$reportedAt -ge 200 -or $file.Length -eq $limit)){
                        Write-DownloadProgress $progressPath $file.Length $limit
                        $reportedAt=$watch.ElapsedMilliseconds
                    }
                }
            } finally {$file.Dispose();$inputStream.Dispose()}
            return
        } finally {$resp.Dispose()}
    }
    throw '更新来源跳转过多'
}
function Get-Catalog {
    $name=if($Product -eq 'cs'){'jubo-live'}else{'jubo-live-rust'}
    $repo="xinshengxia/$name"
    $urls=@("https://raw.githubusercontent.com/$repo/updates/channel.json","https://gcore.jsdelivr.net/gh/$repo@updates/channel.json","https://cdn.jsdelivr.net/gh/$repo@updates/channel.json","https://gh-proxy.com/https://raw.githubusercontent.com/$repo/updates/channel.json")
    $tmp=Join-Path $cache ([guid]::NewGuid().ToString('N')+'.catalog')
    try {
        foreach($url in $urls){
            try {
                Receive-PublicFile $url $tmp 65536 8
                $text=[IO.File]::ReadAllText($tmp);$record=Read-SignedCatalog $text
            }catch{continue}
            # An edge may still hold an older, valid catalog. Never replace a newer verified record.
            if([IO.File]::Exists($catalogPath)){
                try{$previous=Read-SignedCatalog ([IO.File]::ReadAllText($catalogPath))}catch{$previous=$null}
                if($previous -and [version]$previous.version -gt [version]$record.version){return $previous}
            }
            Write-AtomicText $catalogPath $text
            Write-AtomicText (Join-Path $cache 'latest.txt') $record.version
            return $record
        }
        throw '暂时无法取得有效的发布者签名清单。可稍后重试，或导入更新包与签名清单。'
    } finally {if([IO.File]::Exists($tmp)){[IO.File]::Delete($tmp)}}
}
function Get-Package($asset,[string]$ver,[string]$path) {
    $repo=if($Product -eq 'cs'){'jubo-live'}else{'jubo-live-rust'}
    $origin="https://github.com/xinshengxia/$repo/releases/download/v$ver/$($asset.name)"
    $progressPath=Join-Path $requestDir 'download.json'
    foreach($url in @($origin,"https://gh-proxy.com/$origin","https://ghfast.top/$origin")){
        try {
            Write-DownloadProgress $progressPath 0 $asset.size
            Set-ProgressText '正在下载更新包…'
            Receive-PublicFile $url $path ([long]$asset.size) 300 $progressPath
            Set-ProgressText '正在校验下载的更新包…'
            if((Get-Item -LiteralPath $path).Length -eq $asset.size -and (Get-FileHash -LiteralPath $path).Hash -eq $asset.sha256){return}
        }catch{if([IO.File]::Exists($script:cancelFile)){throw '已取消更新'}}
    }
    throw '直连和备用下载线路均未取得通过验证的安装包。可稍后重试或使用本地更新包。'
}
function Safe-Child([string]$root,[string]$relative) {
    if(!$relative -or $relative.Length -gt 400 -or $relative -match '[\\:\x00-\x1f*?"<>|]' -or $relative.StartsWith('/')){throw '安装包路径无效'}
    foreach($part in $relative.Split('/')){
        if(!$part -or $part -eq '.' -or $part -eq '..' -or $part -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw '安装包路径不安全'}
    }
    $base=[IO.Path]::GetFullPath($root).TrimEnd('\')
    $full=[IO.Path]::GetFullPath((Join-Path $base ($relative.Replace('/','\'))))
    if(!$full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw '文件超出安装目录'}
    for($p=$full;$p.Length -ge $base.Length;$p=[IO.Path]::GetDirectoryName($p)){
        if(([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) -and ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint)){throw '安装位置含链接，不能安全替换'}
        if($p -eq $base){break}
    }
    return $full
}
function Is-Managed([string]$rel) {
    if($rel -match '(?i)(^|/)(data|runtime|logs|profiles?|credentials|backups|[^/]*-(login|verify)-profile)(/|$)|(^|/)(live-hub|appsettings|favorites|history)\.json$|\.dpapi$|(^|/)\.env($|\.)|cookies?[^/]*\.txt(\.json)?$'){return $false}
    $top=$rel.Split('/')[0]
    if($Product -eq 'cs'){
        return ($top -in @('assets','licenses','fonts','lib','lua') -or $rel.StartsWith('bin/mpv/',[StringComparison]::OrdinalIgnoreCase) -or $rel -in @('看直播.exe','看直播.exe.config','使用说明.md','emoji-sheet.png','emoji-sheet.txt','deps.lock.json','THIRD_PARTY_NOTICES.md','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','AntdUI.dll','portable.marker','LICENSE','manifest.json'))
    }
    return ($top -in @('assets','licenses','docs') -or $rel -in @('live-hub-rust.exe','README.md','THIRD_PARTY_NOTICES.md','LICENSE','使用说明.md','SOURCE.txt','portable.marker','Cargo.lock','components.json','manifest.json','manifest.json.sha256'))
}
function Expand-VerifiedPackage([string]$zip,[string]$dest,$asset,[string]$version) {
    if((Get-Item -LiteralPath $zip).Length -ne [long]$asset.size -or (Get-FileHash -LiteralPath $zip).Hash -ne $asset.sha256){throw '安装包与签名清单的 SHA-256 不匹配'}
    if(Test-Path -LiteralPath $dest){throw '验包目录必须为空的新位置'}
    [IO.Directory]::CreateDirectory($dest)|Out-Null
    $seen=@{};$total=0L
    $f=[IO.File]::OpenRead($zip);$ar=[IO.Compression.ZipArchive]::new($f,[IO.Compression.ZipArchiveMode]::Read)
    try {
        if($ar.Entries.Count -gt 10000){throw '安装包条目过多'}
        foreach($e in $ar.Entries){
            $rel=$e.FullName;if($rel.EndsWith('/')){continue}
            if($seen.ContainsKey($rel) -or !(Is-Managed $rel) -or (($e.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000){throw '安装包包含非程序文件或重名条目'}
            $total+=$e.Length
            if($e.Length -gt 512MB -or $total -gt 2GB){throw '解压大小超限'}
            $target=Safe-Child $dest $rel;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
            $src=$e.Open();$dst=[IO.File]::Open($target,'CreateNew','Write','None')
            try{$src.CopyTo($dst);if($dst.Length -ne $e.Length){throw '解包不完整'}}finally{$src.Dispose();$dst.Dispose()}
            $seen[$rel]=$true
        }
    } finally {$ar.Dispose();$f.Dispose()}
    $manifest=[IO.File]::ReadAllText((Join-Path $dest 'manifest.json'))|ConvertFrom-Json
    $listed=@{'manifest.json'=$true}
    if($Product -eq 'cs'){
        if($manifest.releaseVersion -ne $version -or $manifest.variant -ne $variant){throw '安装包版本或类型不符'}
        $rows=$manifest.artifacts
    }else{
        if($manifest.version -ne $version -or $manifest.target -ne 'windows-x64'){throw 'Rust 安装包版本或平台不符'}
        $rows=$manifest.files;$listed['manifest.json.sha256']=$true
    }
    foreach($row in $rows){
        $rel=[string]$row.path
        if($listed.ContainsKey($rel) -or !$seen.ContainsKey($rel)){throw '文件清单不完整'}
        $item=Safe-Child $dest $rel
        if((Get-Item -LiteralPath $item).Length -ne [long]$row.size -or (Get-FileHash -LiteralPath $item).Hash -ne $row.sha256){throw '程序文件校验失败'}
        $listed[$rel]=$true
    }
    $required=if($Product -eq 'cs'){'看直播.exe'}else{'live-hub-rust.exe'}
    if($listed.Count -ne $seen.Count -or !$listed.ContainsKey($required) -or !$seen.ContainsKey('assets/updater/Start-Update.ps1')){throw '缺少必需程序文件'}
    return $manifest
}
