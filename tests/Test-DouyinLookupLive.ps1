param([string]$Exe='', [string]$CookieFile='', [string]$Workspace='', [switch]$Browser)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $Exe){$Exe=Join-Path $repo '看直播-mpv.exe'}
if(-not $CookieFile){$CookieFile=Join-Path $repo 'runtime/douyin-cookies.txt.json'}
if(-not $Workspace){$Workspace=Join-Path $repo ('artifacts/douyin-live-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
if(Test-Path -LiteralPath $Workspace){throw 'Live test workspace must be new'}
$workspaceRoot=[IO.Path]::GetFullPath($Workspace)
New-Item -ItemType Directory -Path $workspaceRoot | Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=[Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @($identity,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
Set-Acl -LiteralPath $workspaceRoot -AclObject $acl
$runtime=Join-Path $workspaceRoot 'runtime';New-Item -ItemType Directory -Path $runtime | Out-Null
$cookieBefore=(Get-FileHash -LiteralPath $CookieFile).Hash
Copy-Item -LiteralPath $CookieFile -Destination (Join-Path $runtime 'douyin-cookies.txt.json')
$keywords=if($Browser){@('张大仙')}else{@('董宇辉','张大仙','旭旭宝宝')}
$summary=@()
try {
 foreach($keyword in $keywords){
  $report=Join-Path $workspaceRoot ($keyword+'.json')
  $arguments=@('--lookup-douyin-probe',$keyword,('"'+$report+'"'))
  if($Browser){$arguments+='--browser'}
  $arguments+=('"--runtime='+$runtime+'"')
  $process=Start-Process -WindowStyle Hidden -FilePath ([IO.Path]::GetFullPath($Exe)) -ArgumentList $arguments -PassThru
  if(-not $process.WaitForExit(60000)){Stop-Process -Id $process.Id -Force;throw 'Live search deadline exceeded'}
  if(-not (Test-Path -LiteralPath $report)){throw 'Live search report missing'}
  $data=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
  $summary += [pscustomobject]@{keyword=$keyword;mode=$data.mode;success=$data.success;count=$data.candidate_count;status=$data.status;exit=$process.ExitCode;report=$report}
  if($process.ExitCode -ne 0 -or -not $data.success){throw ('Live search failed for '+$keyword+'; see sanitized report '+$report)}
  if(@($data.candidates | Where-Object {$_.verification -ne 'unverified' -or $_.room_id -ne $null}).Count){throw 'Search candidates were incorrectly treated as verified rooms'}
 }
} finally {
 $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workspaceRoot 'summary.json') -Encoding utf8
 if((Get-FileHash -LiteralPath $CookieFile).Hash -ne $cookieBefore){throw 'Source cookie was changed by live test'}
 $target=[IO.Path]::GetFullPath($runtime)
 if(-not $target.StartsWith($workspaceRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe live test cleanup path'}
 # The process has exited; only this test's copied credential and profile are removed.
 Remove-Item -LiteralPath $target -Recurse -Force
}
$summary | Format-Table -AutoSize
