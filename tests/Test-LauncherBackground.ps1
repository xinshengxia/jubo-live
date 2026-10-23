# Validate the real launcher control tree without showing a window or taking input focus.
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
$arguments = @('--ui-probe', ('"' + $target + '"'))
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(30000)) { throw 'Background control probe timed out' }
    if ($process.ExitCode -ne 0) { throw 'Background control probe failed' }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
$probe = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
$controls = @($probe.controls)
foreach ($label in @('B站','虎牙','斗鱼','抖音','播放器路径','查找主播','加入收藏','删除收藏','用 PotPlayer 打开','用 mpv 打开')) {
    if (-not @($controls | Where-Object { $_.text -like ('*' + $label + '*') }).Count) { throw ('Missing launcher control: ' + $label) }
}
# Since 1.1.0 the launcher is a workbench shell: the chrome, sidebar and
# content panels are top-level, while navigation buttons live inside the
# sidebar (depth 1). Keep the bounds check on those instead of stale
# top-level Button controls from the pre-workbench layout.
$shell = @($controls | Where-Object { $_.depth -eq 0 -and $_.type -match '^Workbench(Strip|Panel)$' })
if ($shell.Count -lt 3) { throw ('No launcher shell panels were inspected: ' + $shell.Count) }
$buttons = @($controls | Where-Object { $_.type -eq 'WorkbenchNavigationButton' })
if ($buttons.Count -lt 8) { throw ('No launcher navigation buttons were inspected: ' + $buttons.Count) }
foreach ($button in $buttons) {
    if ($button.x -lt 0 -or $button.y -lt 0 -or $button.x + $button.w -gt $probe.clientWidth + 2 -or $button.y + $button.h -gt $probe.clientHeight + 2) {
        throw ('Launcher navigation button is outside client bounds: ' + $button.text)
    }
}
Write-Output ('PASS: hidden launcher shell, ' + $buttons.Count + ' navigation buttons and bounds; no desktop window shown')
