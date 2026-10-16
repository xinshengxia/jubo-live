param(
    [string]$Exe = '',
    [string]$Platform = 'bili',
    [string]$Keyword = '鬼叔',
    [string]$Model = '',
    [ValidateSet('Native','Ai','Optimize')][string]$Mode = 'Native',
    [switch]$Deep,
    [switch]$Verify,
    [switch]$Refresh,
    [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
# A PowerShell scriptblock cannot run on provider worker threads. Use a CLR no-op delegate.
Add-Type -TypeDefinition 'public static class LookupProbeProgress { public static void Ignore(string message) {} }'
$progress = [Action[string]][Delegate]::CreateDelegate([Action[string]], [LookupProbeProgress].GetMethod('Ignore'))
if (-not $Exe) { $Exe = Join-Path (Split-Path $PSScriptRoot -Parent) '看直播-mpv.exe' }
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Exe))
$hub = $assembly.GetType('LiveHub')
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
$provider = $hub.GetNestedType('DeepSeekLookup', $flags)
if ($Model) { throw '模型由本机 DeepSeek 设置选择；诊断脚本不覆盖持久配置。' }
$runtime = Join-Path (Split-Path ([IO.Path]::GetFullPath($Exe)) -Parent) 'runtime'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$hub.GetField('RuntimeDir', $flags).SetValue($null, $runtime)
$record = [ordered]@{ at=[DateTime]::UtcNow.ToString('o'); mode=$Mode; provider=if($Mode -eq 'Native'){'platform_http'}else{'deepseek_official'}; platform=$Platform;
    keyword=$Keyword; model=if($Mode -eq 'Native'){$null}else{$hub.GetNestedType('DeepSeekLookupConfig',$flags).GetMethod('Load',$flags).Invoke($null,@()).model}; strategy=if($Mode -eq 'Native'){$null}else{'native_web_search'}; success=$false }
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $verifierType = $hub.GetNestedType('LookupVerifier', $flags)
    $verifier = [Activator]::CreateInstance($verifierType, $true)
    if ($Mode -eq 'Native') {
        $serviceType = $hub.GetNestedType('LookupService', $flags)
        $service = [Activator]::CreateInstance($serviceType, $true)
        if ($Refresh) { $result = $serviceType.GetMethod('Refresh', $flags).Invoke($service, @($Platform, $Keyword, [Threading.CancellationToken]::None, $progress)) }
        else { $result = $serviceType.GetMethod('Search', $flags).Invoke($service, @($Platform, $Keyword, $false, $false, [Threading.CancellationToken]::None, $progress)) }
    } elseif ($Mode -eq 'Optimize') {
        $native = $verifierType.GetMethod('SearchNative', $flags).Invoke($verifier, @($Platform, $Keyword, [Threading.CancellationToken]::None))
        $serviceType = $hub.GetNestedType('LookupService', $flags)
        $service = [Activator]::CreateInstance($serviceType, $true)
        $result = $serviceType.GetMethod('Improve', $flags).Invoke($service, @($Platform, $Keyword, $native.candidates, [Threading.CancellationToken]::None, $progress))
    } else {
        $result = $provider.GetMethod('Search', $flags).Invoke($null, @($Platform, $Keyword, [bool]$Deep, [Threading.CancellationToken]::None, $progress))
    }
    $record.stream_completed = $Mode -ne 'Native'
    $record.success = $result.candidates.Count -gt 0
    $record.usable_candidates = $result.candidates.Count
    $record.first_source_ms = $result.first_source_ms
    $record.candidate_ms = $result.candidate_ms
    $record.usage = $result.usage
    $record.planning_usage = $result.planning_usage; $record.research_queries = @($result.research_queries)
    $record.model_candidates = $result.model_candidates; $record.rejected_candidates = $result.rejected_candidates
    $record.search_sources = @($result.sources)
    $record.status = $result.status; $record.note = $result.note
    $record.platform_outcomes = @($result.platforms); $record.searched_at = $result.searched_at; $record.reviews = @($result.reviews)
    $record.candidates = @($result.candidates | ForEach-Object {
        [ordered]@{ platform=$_.platform; display_name=$_.display_name; account_id=$_.account_id; sec_uid=$_.sec_uid; account_handle=$_.account_handle;
            room_id=$_.room_id; session_room_id=$_.session_room_id; profile_url=$_.profile_url; room_url=$_.room_url;
            verification=$_.verification; category=$_.category; description=$_.description; avatar_url=$_.avatar_url; relevance=$_.relevance; relevance_note=$_.relevance_note; platform_update=$_.platform_update; update_sources=@($_.update_sources); sources=@($_.sources); match_reasons=$_.match_reasons }
    })
    if ($Verify -and $result.candidates.Count -gt 0) {
        $verifierType = $hub.GetNestedType('LookupVerifier', $flags)
        $verifier = [Activator]::CreateInstance($verifierType, $true)
        $record.verifications = @()
        for ($index = 0; $index -lt [Math]::Min(3, $result.candidates.Count); $index++) {
            $candidate = $result.candidates[$index]
            $verifyWatch = [Diagnostics.Stopwatch]::StartNew()
            $invokeArgs = [object[]]::new(2)
            $invokeArgs[0] = $candidate.PSObject.BaseObject; $invokeArgs[1] = [Threading.CancellationToken]::None
            $verified = $verifierType.GetMethod('Verify', $flags).Invoke($verifier, $invokeArgs)
            $record.verifications += [ordered]@{ elapsed_ms=$verifyWatch.ElapsedMilliseconds; state=$verified.verification;
                display_name=$verified.display_name; note=$verified.verification_note; room_id=$verified.room_id; account_id=$verified.account_id;
                account_handle=$verified.account_handle; room_url=$verified.room_url; account_verified=$verified.account_verified;
                resolution_path=$verified.resolution_path; live_status=$verified.live_status; sources=@($verified.sources) }
        }
        $record.platform_verification = $record.verifications[0]
    }
}
catch {
    $record.error_context = $_.ScriptStackTrace
    $failure = $_.Exception
    while ($failure.InnerException) { $failure = $failure.InnerException }
    # Only locally authored failure messages are recorded, never a raw service body or HTTP headers.
    if ($failure.GetType().Name -eq 'LookupFailure') {
        $record.error = $failure.Message
        $record.error_code = $failure.GetType().GetField('Code', $flags).GetValue($failure)
    } else { $record.error = $failure.GetType().Name }
}
$record.elapsed_ms = $watch.ElapsedMilliseconds
$json = $record | ConvertTo-Json -Depth 12
if ($Report) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($Report), $json, [Text.UTF8Encoding]::new($false)) }
Write-Output $json
if ($record.error -or -not $record.success) { exit 1 }
