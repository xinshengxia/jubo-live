param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$version = '0.0.0'; $commit = 'nogit'; $stamp = 'unknown'
if (Get-Command git -ErrorAction SilentlyContinue) {
    $head = & git -C $Repo rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $head -match '^[a-f0-9]{40}$') {
        $commit = $head.Substring(0, 12)
        $tag = & git -C $Repo describe --tags --abbrev=0 2>$null
        if ($LASTEXITCODE -eq 0 -and $tag -match '^v?(\d+\.\d+\.\d+)$') { $version = $Matches[1] }
        $state = @(& git -C $Repo status --porcelain --untracked-files=normal)
        if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 工作区状态' }
        if ($state.Count) { $commit += '-dirty' }
        # 构建时间取 commit 时间，使同一个干净快照的两条编译路径获得完全相同的 build ID。
        $epoch = & git -C $Repo show -s --format=%ct HEAD
        $stamp = [DateTimeOffset]::FromUnixTimeSeconds([long]$epoch).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
        if ($LASTEXITCODE -ne 0) { throw '无法读取 Git commit 时间' }
    }
}
$numeric = $version + '.0'
$info = "$version+$commit ($stamp)"
$destination = [IO.Path]::GetFullPath($Out)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
@"
using System.Reflection;
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$info")]
[assembly: AssemblyProduct("看直播 live-hub")]
[assembly: AssemblyDescription("四平台直播启动器 + 弹幕覆盖层 (commit $commit)")]
"@ | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output "BUILD=$info"
