# 可独立离线验收；Build-Portable 在成功宣告之前调用。
function Find-PackageCredentials {
    param([Parameter(Mandatory)][string]$Package)
    $root = Get-Item -LiteralPath $Package -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '凭据扫描要求一个非重解析目录'
    }
    $items = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force -ErrorAction Stop)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw '包内含重解析项，无法保证凭据扫描/剥离范围'
    }
    $items | Where-Object {
        ($_.PSIsContainer -and ($_.Name -match '^profiles?$|-(login|verify)-profile$')) -or
        (-not $_.PSIsContainer -and ($_.Name -match 'cookies.*\.txt(\.json)?$|cookie.*\.credential\.tmp$|\.dpapi$' -or
            $_.FullName -match '\\profiles?\\|-(login|verify)-profile\\'))
    }
}

function Protect-PackageCredentials {
    param([Parameter(Mandatory)][string]$Package, [switch]$IncludeCredentials)
    $root = (Get-Item -LiteralPath $Package -Force -ErrorAction Stop).FullName.TrimEnd('\') + '\'
    $credentials = @(Find-PackageCredentials -Package $Package)
    foreach ($item in $credentials) { Write-Output ('  · ' + $item.FullName.Substring($root.Length)) }
    if ($IncludeCredentials) {
        Write-Output '  已按 -IncludeCredentials 保留凭据（仅限自用迁移）'
        return
    }
    # 先处理较深项；被占用、枚举失败或删除失败均使构建失败，不会打印成功。
    foreach ($item in @($credentials | Sort-Object { $_.FullName.Length } -Descending)) {
        $target = [IO.Path]::GetFullPath($item.FullName)
        if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw '凭据对象超出包目录' }
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
    }
    $remaining = @(Find-PackageCredentials -Package $Package)
    if ($remaining.Count) { throw "剥离后复扫仍有 $($remaining.Count) 个凭据对象" }
    Write-Output ('  已剥离 ' + $credentials.Count + ' 个凭据对象；复扫 0 命中')
}
