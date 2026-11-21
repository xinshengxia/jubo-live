param([Parameter(Mandatory)][string]$Workspace,[string]$Source=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $Workspace){throw 'New fixture directory required'}
[IO.Directory]::CreateDirectory($Workspace)|Out-Null
$Product='cs';$variant='lite';$devLayout=$false;$exeName='看直播.exe'
$code=Join-Path $Workspace 'code'
Copy-Item -LiteralPath (Join-Path $Source 'assets/updater') -Destination $code -Recurse
. (Join-Path $code 'Core.ps1')
. (Join-Path $code 'Install.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture=Join-Path $Workspace 'fixture'
[IO.Directory]::CreateDirectory("$fixture/assets/updater")|Out-Null
[IO.File]::WriteAllText("$fixture/看直播.exe",'fixture replacement')
[IO.File]::WriteAllText("$fixture/assets/updater/Start-Update.ps1",'# synthetic input')
$rows=@(foreach($f in Get-ChildItem -LiteralPath $fixture -File -Recurse){
    @{path=$f.FullName.Substring($fixture.Length+1).Replace('\','/');size=$f.Length;sha256=(Get-FileHash $f.FullName).Hash}
})
Write-AtomicText "$fixture/manifest.json" (@{releaseVersion='1.0.1';variant='lite';artifacts=$rows}|ConvertTo-Json -Depth 4)
$zip="$Workspace/jubo-live-1.0.1-win-x64-lite.zip"
$stream=[IO.File]::Open($zip,'CreateNew','Write','None')
$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($f in Get-ChildItem -LiteralPath $fixture -File -Recurse){
    $entry=$archive.CreateEntry($f.FullName.Substring($fixture.Length+1).Replace('\','/'))
    $out=$entry.Open();$src=$f.OpenRead();try{$src.CopyTo($out)}finally{$src.Dispose();$out.Dispose()}
}}finally{$archive.Dispose();$stream.Dispose()}
$asset=@{name=[IO.Path]::GetFileName($zip);size=(Get-Item $zip).Length;sha256=(Get-FileHash $zip).Hash}
$second=$asset.Clone();$second.name='jubo-live-1.0.1-win-x64-full.zip'
$record=@{product='cs';version='1.0.1';schema=1;assets=@($asset,$second)}
$bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress -Depth 5))
$rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(2048,[Security.Cryptography.CspParameters]::new(24));$rsa.PersistKeyInCsp=$false
try {
    Write-AtomicText "$code/channel-key.xml" $rsa.ToXmlString($false)
    $envelope=@{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($rsa.SignData($bytes,'SHA256'))}
    $verified=Read-SignedCatalog ($envelope|ConvertTo-Json -Compress)
    if($verified.version -ne '1.0.1'){throw 'signed fixture'}
    $envelope.payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{}'))
    $blocked=$false;try{$null=Read-SignedCatalog ($envelope|ConvertTo-Json -Compress)}catch{$blocked=$true};if(!$blocked){throw 'tampered catalog accepted'}
}finally{$rsa.Dispose()}
$dest=Join-Path $Workspace 'stage'
$null=Expand-VerifiedPackage $zip $dest $asset '1.0.1'
$bad=$asset.Clone();$bad.sha256='0'*64;$blocked=$false
try{$null=Expand-VerifiedPackage $zip ($dest+'-bad') $bad '1.0.1'}catch{$blocked=$true};if(!$blocked){throw 'tampered digest accepted'}
foreach($badPath in '../fixture','/absolute','a:b','foo/../bar','NUL','foo\bar','file.'){
    $blocked=$false;try{$null=Safe-Child $dest $badPath}catch{$blocked=$true};if(!$blocked){throw 'unsafe path accepted'}
}
$installRoot=Join-Path $Workspace 'installed'
Write-AtomicText "$installRoot/看直播.exe" 'old fixture'
Write-AtomicText "$installRoot/data/fixture.txt" 'private fixture'
Write-AtomicText "$installRoot/live-hub.json" '{}'
Install-InPlace $dest
if([IO.File]::ReadAllText("$installRoot/看直播.exe") -ne 'fixture replacement' -or [IO.File]::ReadAllText("$installRoot/data/fixture.txt") -ne 'private fixture' -or [IO.File]::ReadAllText("$installRoot/live-hub.json") -ne '{}'){throw 'replacement or private state incorrect'}
$prior=[IO.File]::ReadAllText("$installRoot/.jubo-updates/previous.txt")
if([IO.File]::ReadAllText("$installRoot/.jubo-updates/$prior/files/看直播.exe") -ne 'old fixture'){throw 'rollback missing'}
Write-Output 'PASS signed catalog, tampering, zip digest, path constraints, in-place update, data preservation and backup'


# Force a mid-transaction failure after changing a different managed file.
Write-AtomicText "$dest/assets/updater/Start-Update.ps1" '# next fixture'
Write-AtomicText "$dest/看直播.exe" 'second fixture'
$lock=[IO.File]::Open("$installRoot/看直播.exe",'Open','Read','Read')
try{
    $failed=$false;try{Install-InPlace $dest}catch{$failed=$true}
    if(!$failed){throw 'locked target unexpectedly updated'}
}finally{$lock.Dispose()}
if([IO.File]::ReadAllText("$installRoot/assets/updater/Start-Update.ps1") -ne '# synthetic input' -or
    [IO.File]::ReadAllText("$installRoot/看直播.exe") -ne 'fixture replacement'){throw 'partial installation not restored'}
$Product='rust'
$installRoot=Join-Path $Workspace 'artifacts/release-1.0.0';[IO.Directory]::CreateDirectory($installRoot)|Out-Null
$ticket=[guid]::NewGuid().ToString('N');$junction=Join-Path (Split-Path $installRoot -Parent) 'current'
New-Item -Path $junction -ItemType Junction -Target $installRoot|Out-Null
Install-ManagedRust $dest '1.0.1'
if((Get-Item $junction -Force).Target -ne (Join-Path (Split-Path $installRoot -Parent) 'release-1.0.1') -or !(Test-Path $installRoot)){throw 'version switch or rollback lost'}
# Remove only the disposable test junction, never follow its target.
if((Get-Item $junction -Force).LinkType -ne 'Junction'){throw 'unexpected test link'}
[IO.Directory]::Delete($junction,$false)
Write-Output 'PASS in-place failure rollback and Rust version directory switch'
