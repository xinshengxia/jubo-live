param(
    [Parameter(Mandatory)][ValidateSet('cs','rust')][string]$Product,
    [string]$Version, [string]$PackageDirectory, [string]$Output,
    [string]$KeyFile = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JuboLivePublisher/channel-key.dpapi'),
    [switch]$Initialize
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
$entropy=[Text.Encoding]::UTF8.GetBytes('JuboLive.UpdateChannel.SigningKey.v1')
$scope=[Security.Cryptography.DataProtectionScope]::CurrentUser
$rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(3072,[Security.Cryptography.CspParameters]::new(24))
$rsa.PersistKeyInCsp=$false
try {
    if ($Initialize -and !(Test-Path -LiteralPath $KeyFile)) {
        $dir=Split-Path -Parent $KeyFile
        [IO.Directory]::CreateDirectory($dir)|Out-Null
        $plain=[Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        try {$cipher=[Security.Cryptography.ProtectedData]::Protect($plain,$entropy,$scope)}
        finally {[Array]::Clear($plain,0,$plain.Length)}
        $f=[IO.File]::Open($KeyFile,'CreateNew','Write','None')
        try {$f.Write($cipher,0,$cipher.Length);$f.Flush($true)} finally {$f.Dispose()}
    }
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile),$entropy,$scope)
    try {$rsa.FromXmlString([Text.Encoding]::UTF8.GetString($plain))} finally {[Array]::Clear($plain,0,$plain.Length)}
    if ($Initialize) {
        [IO.File]::WriteAllText($Output,$rsa.ToXmlString($false),[Text.UTF8Encoding]::new($false))
        return
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {throw 'Expected a stable release version'}
    $names=if($Product -eq 'cs'){@("jubo-live-$Version-win-x64-full.zip","jubo-live-$Version-win-x64-lite.zip")}else{@("jubo-live-rust-$Version-win-x64-lite.zip")}
    $assets=@(foreach($name in $names){
        $files=@(Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse -Filter $name)
        if($files.Count -ne 1){throw "Expected exactly one $name"}
        $file=$files[0];$digest=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $side=[IO.File]::ReadAllText($file.FullName+'.sha256').Trim()
        if($side -cne ($digest+'  '+$name)){throw "Checksum mismatch: $name"}
        [ordered]@{name=$name;sha256=$digest;size=$file.Length}
    })
    $json=[ordered]@{schema=1;product=$Product;version=$Version;assets=$assets}|ConvertTo-Json -Compress -Depth 4
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)
    $signature=$rsa.SignData($bytes,'SHA256')
    $record=[ordered]@{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)}
    [IO.File]::WriteAllText($Output,($record|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
} finally {$rsa.Dispose()}
