# Bound child processes, drain both output streams, and terminate only the owned tree.
function Invoke-BoundedProcess {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$Log, [ValidateRange(1,900)][int]$TimeoutSeconds = 300)
    Write-Output ('STAGE: ' + [IO.Path]::GetFileName($Log))
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Log))) | Out-Null
    [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    $exitCode = $process.ExitCode; $process.Dispose()
    if ($timedOut -or $exitCode -ne 0) {
        Get-Content -LiteralPath $Log -Tail 25 | Write-Output
        throw ('Child stage failed: ' + [IO.Path]::GetFileName($Log) + '; timeout=' + $timedOut + '; exit=' + $exitCode)
    }
    Write-Output ('PASS: ' + [IO.Path]::GetFileName($Log))
}
