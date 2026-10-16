param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$bundle=Join-Path $SourceRoot 'lib/webview2'
$manifest=Get-Content -LiteralPath (Join-Path $bundle 'assets.json') -Raw | ConvertFrom-Json
if($manifest.package -ne 'Microsoft.Web.WebView2' -or $manifest.version -ne '1.0.4258.31' -or @($manifest.files).Count -ne 4){throw 'WebView2 asset manifest is invalid'}
foreach($item in $manifest.files){
 if($item.path -notmatch '^(Microsoft\.Web\.WebView2\.(Core|WinForms)\.dll|(x64|x86)/WebView2Loader\.dll)$'){throw 'Invalid WebView2 asset path'}
 $source=Join-Path $bundle $item.path
 if((Get-FileHash -LiteralPath $source).Hash -ne $item.sha256 -or (Get-Item -LiteralPath $source).Length -ne $item.size){throw ('WebView2 asset mismatch: '+$item.path)}
 $targets=@((Join-Path (Join-Path $Destination 'lib/webview2') $item.path))
 if($item.path -notmatch '/'){$targets+=Join-Path $Destination $item.path}
 foreach($target in $targets){
  if([IO.Path]::GetFullPath($source) -eq [IO.Path]::GetFullPath($target)){continue}
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
  Copy-Item -LiteralPath $source -Destination $target -Force
 }
}
$to=Join-Path $Destination 'lib/webview2/assets.json'
if([IO.Path]::GetFullPath($to) -ne [IO.Path]::GetFullPath((Join-Path $bundle 'assets.json'))){Copy-Item -LiteralPath (Join-Path $bundle 'assets.json') -Destination $to -Force}
Write-Output 'WebView2 SDK 固定哈希校验及资源同步通过。'
