# Publish-Release.ps1 —— 可复现发布流水线（外部评估报告 §6 要求的那条链路）
#
# 结论先行：本脚本只认「一个干净的 commit」当输入，产出「ZIP + SHA-256 + manifest.json」，中间每一步都可复核：
#
#   干净 commit -> 新建 staging（仓库同级全新克隆）-> 按 deps.lock.json 复制固定版本依赖（逐个校验 sha256）
#   -> 编译一次 -> 完整离线回归 + GUI 冒烟 -> 扫描并剥离凭据 -> 打 ZIP -> 写 manifest.json + <zip>.sha256
#
# 为什么这么较真（评估报告 §6 的原话）：现在的绿色包构建「依赖一个已经存在且装好 mpv/PotPlayer/yt-dlp 的
# 外部目录；直接包路径使用 32 位 legacy csc；不带 src\build.ps1 的包只编译和自检、不跑完整离线回归；
# 仓库没有统一的依赖锁定清单和发布哈希」。四条的共同后果是：同一份源码换个时间/换台机器，构建出来的包
# 可能不一样，而且没东西能证明它一样或不一样。本脚本把每个「可能不一样」的点都钉成可校验的哈希或退出码。
#
# 用法：
#   pwsh -NoProfile -File .\tools\Publish-Release.ps1                     # 输出到 <仓库同级>\livehub-release-<短commit>\
#   pwsh -NoProfile -File .\tools\Publish-Release.ps1 -Out .\artifacts\release          # 换输出目录
#   pwsh -NoProfile -File .\tools\Publish-Release.ps1 -IncludeCredentials  # 自用便携包：保留登录态（默认剥离）
#   pwsh -NoProfile -File .\tools\Publish-Release.ps1 -KeepStaging         # 排查用：构建完保留 staging
#
# 退出码：0 成功 / 2 参数或工作区不干净 / 3 构建或离线回归失败 / 4 GUI 冒烟失败 / 5 依赖哈希不匹配 / 6 打包失败
param(
    [string]$Repo = '',              # 发布源仓库（默认：本脚本所在仓库）
    [string]$Out = '',               # 输出目录（默认：<仓库同级>\livehub-release-<短commit>）
    [string]$DependencyRoot = '', # Read-only external components; or LIVEHUB_DEPENDENCY_ROOT.
    [string]$LockFile = '',          # 依赖锁定清单（默认：<仓库>\deps.lock.json）
    [switch]$IncludeCredentials,     # 保留登录 Cookie / 浏览器 profile（默认剥离，安全侧）
    [switch]$KeepStaging,            # 保留 staging 目录（默认成功后删除）
    [switch]$SkipGuiSmoke,           # 逃逸阀：跳过 GUI 冒烟（默认必跑；用了会在 manifest 里留痕）
    [switch]$Headless,               # 跳过需原生桌面交互的回归，仍执行隐藏控件冒烟；清单保留跳过项。
    [switch]$AllowDirtyForTest,      # 仅自测：跳过「脏工作区拒绝」。正式发布永远不要加这个开关
    # 打包裁剪（2026-09-27 定稿）：默认只打**可运行绿色包** —— 拿到就能跑。
    #   · 不含源码/工具/文档/测试（那是开发树的东西，发布包要它们只会让包变大、还泄露实现细节）；
    #   · 不含个人数据（收藏/历史）—— 它们是作者的使用痕迹，不是软件的一部分；
    #   · 凭据始终按 -IncludeCredentials 处理（默认剥离）。
    # 需要「带源码的自重建包」（加固版那种用法）加 -IncludeSources；自用便携包加 -IncludeUserData。
    [switch]$IncludeSources,
    [switch]$IncludeUserData,
    # 绿色包变体（2026-10-16）：
    #   full = 自带 mpv；lite = 不带播放器（用户在界面里指自己的 mpv/PotPlayer）。
    # PotPlayer **不进公开包**：它是专有免费软件，EULA 不授予再分发权；mpv 是 GPL/LGPL，可随包（附许可）。
    [ValidateSet('full', 'lite')][string]$Variant = 'full',
    # PotPlayer **默认不进包**：它是专有免费软件（Kakao），EULA 不授予再分发权，
    # 放进公开的 GitHub Release 有法律风险。程序**支持**用 PotPlayer 播放，
    # 但由用户自备（界面「本地数据 / 平台状态 → 选择 PotPlayer…」指定）。
    # 只有自用便携包才加这个开关。
    [switch]$IncludePotPlayer
)
$ErrorActionPreference = 'Stop'
# 子进程输出按 UTF-8 解码：控制台代码页可能与子进程（pwsh/git/dotnet）的管道 UTF-8 字节不同，
# 使用不匹配的代码页解码会导致子进程输出乱码。
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

$pipelineStart = Get-Date
$script:failInfo = $null
$script:staging = ''
$script:lastExit = 0
$script:exitCode = 0
$script:summary = $null
$script:srcPackages = @{}

function Say([string]$text) { Write-Output $text }
function Step([string]$text) { Write-Output ''; Write-Output ('==> ' + $text) }
function Fatal([string]$stage, [string]$message, [string]$fix, [int]$code) {
    # 统一的「哪一步 + 怎么办」出口：不直接 exit，交给顶层 catch 打印并决定退出码（这样收尾一定跑到）。
    $script:failInfo = [pscustomobject]@{ Stage = $stage; Message = $message; Fix = $fix; Code = $code }
    throw (New-Object System.Exception ($stage + '：' + $message))
}

function Get-Sha256([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function Get-RelPath([string]$baseDir, [string]$fullPath) {
    $rel = $fullPath.Substring($baseDir.Length).TrimStart([char]92, [char]47)
    return ($rel -replace '\\', '/')
}

function Get-TreeHash([object[]]$entries) {
    # 算法与 deps.lock.json 的 treeHashRule 一字不差：
    #   按小写相对路径（/ 分隔）排序，逐行拼 <小写相对路径>LF<大写 sha256>LF，取 UTF-8 字节的 SHA-256（小写 hex）。
    $sb = New-Object System.Text.StringBuilder
    foreach ($e in $entries) {
        [void]$sb.Append($e.Path.ToLowerInvariant()).Append([char]10).Append($e.Hash).Append([char]10)
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    return (-join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }))
}

function Convert-GlobToRegex([string]$glob) {
    # 只支持 deps.lock.json 里用到的那两种：** 跨目录，* 不跨目录。
    $rx = [regex]::Escape($glob)
    $rx = $rx.Replace('\*\*', '.*').Replace('\*', '[^/]*')
    return ('^' + $rx + '$')
}

function Invoke-Native([string]$exe, [string[]]$exeArgs) {
    # 统一收口外部命令：$ErrorActionPreference='Stop' 下子进程写 stderr 会变成终止错误，
    # 所以这里临时降级为 Continue，成败一律看 $LASTEXITCODE。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $exe @exeArgs 2>&1 | ForEach-Object { [string]$_ })
        $script:lastExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    return $lines
}

function Resolve-LockSource([string]$ref) {
    # 清单里的 source 写法：<sourcePackages.id>:<包内相对路径>，例如 portable:bin/mpv/mpv.exe
    $idx = $ref.IndexOf(':')
    if ($idx -lt 1) { Fatal '依赖' ('deps.lock.json 里的 source 写法不认识：' + $ref) '应为 <sourcePackages.id>:<包内相对路径>' 5 }
    $id = $ref.Substring(0, $idx)
    $rel = $ref.Substring($idx + 1)
    if (-not $script:srcPackages.ContainsKey($id)) { Fatal '依赖' ('source 引用了清单里没有的来源 id：' + $id) '检查 deps.lock.json 的 sourcePackages' 5 }
    $full = Join-Path $script:srcPackages[$id] ($rel -replace '/', '\')
    return $full
}

function Assert-FileMatchesLock([string]$path, [string]$expectSha, [long]$expectSize, [string]$what) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fatal '依赖' ('锁定的组件不存在：' + $what + ' -> ' + $path) '绿色包目录被移动/改名了？改 deps.lock.json 的 sourcePackages 后重跑' 5
    }
    $actual = (Get-Item -LiteralPath $path).Length
    if ($actual -ne $expectSize) {
        Fatal '依赖' ('组件大小不符：' + $what + ' 期望 ' + $expectSize + ' 字节，实际 ' + $actual + ' 字节（' + $path + '）') '源文件被换过。要么恢复原文件，要么重新生成 deps.lock.json 并提交' 5
    }
    $hash = Get-Sha256 $path
    if ($hash -ne $expectSha.ToUpperInvariant()) {
        Fatal '依赖' ('组件 sha256 不符：' + $what + ' 期望 ' + $expectSha.ToUpperInvariant() + '，实际 ' + $hash + '（' + $path + '）') '源文件被换过。流水线不会自动绕过哈希校验：确认无误后重新生成 deps.lock.json 并提交' 5
    }
    return $hash
}

function Show-TestFailure([string[]]$buildOut) {
    # 评估报告 §2 的整改点：失败时「第一条 FAIL / 失败用例名 / 完整日志路径」必须一眼可见。
    $firstFail = @($buildOut | Where-Object { $_ -match '^FAIL' } | Select-Object -First 1)
    $caseFail = @($buildOut | Where-Object { $_ -match '^FAILED-CASE:' })
    # 成功时 Build-Hardened 打印「离线测试日志： <path>」，失败时打印「完整日志： <path>」—— 两个都要认，
    # 否则失败路径会把「完整日志」显示成「构建没跑到测试那一步」（第一版就是这样误导人的）。
    $logLine = @($buildOut | Where-Object { $_ -match '离线测试日志： ' -or $_ -match '完整日志： ' } | Select-Object -Last 1)
    Say '  +-- 离线回归失败（这三样必须在最前面）---------------------------'
    if ($firstFail.Count -gt 0) { Say ('  | 第一条失败    : ' + $firstFail[0]) }
    else { Say '  | 第一条失败    : 没有 FAIL 行 —— 多半是编译就失败了，见下面的构建输出' }
    if ($caseFail.Count -gt 0) {
        foreach ($l in $caseFail) { Say ('  | 失败用例      : ' + $l) }
    } else { Say '  | 失败用例      : 无 FAILED-CASE 行' }
    if ($logLine.Count -gt 0) { Say ('  | 完整日志      : ' + $logLine[0].Trim().Replace('离线测试日志： ', '').Replace('完整日志： ', '')) }
    else { Say '  | 完整日志      : 构建没跑到测试那一步' }
    Say '  +-----------------------------------------------------------------'
}

# ==========================================================================
try {
    # ── 0. 参数与路径 ─────────────────────────────────────────────────────
    $repoRoot = if ($Repo) { [IO.Path]::GetFullPath($Repo) } else { Split-Path -Parent $PSScriptRoot }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git') -PathType Container)) {
        Fatal '参数' ($repoRoot + ' 不是 git 仓库（没有 .git）') '用 -Repo 指向 live-hub 仓库根，或直接在仓库的 tools 下运行本脚本' 2
    }
    $lockPath = if ($LockFile) { [IO.Path]::GetFullPath($LockFile) } else { Join-Path $repoRoot 'deps.lock.json' }
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        Fatal '参数' ('依赖锁定清单不存在：' + $lockPath) 'deps.lock.json 应放在仓库根；或用 -LockFile 指定' 2
    }

    Say '=== live-hub 发布流水线 ==='
    Say ('  仓库     : ' + $repoRoot)
    Say ('  依赖清单 : ' + $lockPath)
    $credMode = if ($IncludeCredentials) { '保留（-IncludeCredentials）' } else { '剥离（默认，安全侧）' }
    Say ('  凭据策略 : ' + $credMode)

    # ── 1. 只从干净 commit 发布 ──────────────────────────────────────────
    Step '[1/9] 校验发布源：必须是干净的 git 工作区（脏就拒绝，不给「警告后继续」的余地）'
    $headOut = @(Invoke-Native 'git' @('-C', $repoRoot, 'rev-parse', 'HEAD'))
    if ($script:lastExit -ne 0 -or $headOut.Count -eq 0) { Fatal '发布源' '取不到 HEAD（git rev-parse HEAD 失败）' '确认仓库是有效且完整的 git 工作区' 2 }
    $headFull = $headOut[0].Trim()
    $headShort = $headFull.Substring(0, 8)
    $dirtyLines = @(Invoke-Native 'git' @('-C', $repoRoot, '-c', 'core.quotepath=false', 'status', '--porcelain=v1'))
    if ($dirtyLines.Count -gt 0 -and -not $AllowDirtyForTest) {
        Say ''
        Say ('X 拒绝发布：工作区不干净（' + $dirtyLines.Count + ' 个未提交/未跟踪的改动）')
        Say '  为什么拒绝：发布物必须能被一个 commit 唯一标识。带着未提交改动打出来的包，'
        Say '  事后无法回答「包里到底是哪份代码」—— 而评估报告 §6 要的正是这条可追溯性。'
        Say '  脏文件：'
        foreach ($line in ($dirtyLines | Select-Object -First 20)) { Say ('    ' + $line) }
        if ($dirtyLines.Count -gt 20) { Say ('    …还有 ' + ($dirtyLines.Count - 20) + ' 个') }
        Say '  怎么办：先 git add/commit（或 git stash）把这些改动落定，再重跑本脚本。'
        Say '          仅自测可加 -AllowDirtyForTest；注意 staging 是新克隆，未提交改动不会进包。'
        Fatal '发布源' ('工作区不干净：' + $dirtyLines.Count + ' 个文件未提交') '先 commit 或 stash 再发布；仅自测可加 -AllowDirtyForTest' 2
    }
    if ($dirtyLines.Count -gt 0) {
        Say ('  ! -AllowDirtyForTest：工作区有 ' + $dirtyLines.Count + ' 个改动，本应拒绝。')
        Say '    注意 staging 来自 git clone，只含已提交内容 —— 未提交的改动不会进发布包。'
    }
    Say ('  HEAD = ' + $headFull + '   （短号 ' + $headShort + '）')
    Say '  工作区干净 OK'

    # ── 2. 输出目录 ──────────────────────────────────────────────────────
    $outDir = if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path (Split-Path -Parent $repoRoot) ('jubo-release-' + $headShort) }
    $releaseVersion = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Repo $repoRoot
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Step '[2/9] 准备输出目录'
    Say ('  输出目录 : ' + $outDir)
    $logPath = Join-Path $outDir ('publish-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    try { Start-Transcript -Path $logPath | Out-Null } catch { Say '  （提示：Start-Transcript 不可用，本次不落发布日志）' }
    Say ('  发布日志 : ' + $logPath)

    # ── 3. 读依赖清单 ────────────────────────────────────────────────────
    Step '[3/9] 读取依赖锁定清单 deps.lock.json'
    $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $lock.sourcePackages) {
        $sourcePath = [string]$p.path
        if ([string]$p.id -eq 'installed') {
            $sourcePath = if ($DependencyRoot) { $DependencyRoot } else { [Environment]::GetEnvironmentVariable('LIVEHUB_DEPENDENCY_ROOT') }
            if (-not $sourcePath) { Fatal '依赖' '需要显式指定外部依赖目录' '传 -DependencyRoot 或设置 LIVEHUB_DEPENDENCY_ROOT；不要把本机路径写进锁文件' 5 }
        }
        if (-not [IO.Path]::IsPathRooted($sourcePath)) { $sourcePath = Join-Path $repoRoot $sourcePath }
        $script:srcPackages[[string]$p.id] = [IO.Path]::GetFullPath($sourcePath)
    }
    # ConvertFrom-Json 会把 ISO 时间串自动转成 DateTime，这里统一格式化回 ISO，免得 manifest 里出现本地化日期。
    $lockGeneratedAt = [string]$lock.generatedAt
    if ($lock.generatedAt -is [datetime]) { $lockGeneratedAt = $lock.generatedAt.ToString('yyyy-MM-ddTHH:mm:sszzz') }
    Say ('  schema   : ' + $lock.schema + '（生成于 ' + $lockGeneratedAt + '）')
    foreach ($p in $lock.sourcePackages) {
        $resolvedSource = $script:srcPackages[[string]$p.id]
        $exists = Test-Path -LiteralPath $resolvedSource -PathType Container
        $mark = if ($exists) { '  [存在]' } else { '  [缺失]' }
        Say ('  来源     : [' + $p.id + '] ' + $p.path + '  —— ' + $p.role + $mark)
        if (-not $exists) { Fatal '依赖' ('清单里的二进制来源不存在：' + $p.path) '检查 -DependencyRoot 或 LIVEHUB_DEPENDENCY_ROOT；依赖目录只读' 5 }
    }
    Say ('  锁定组件 : ' + @($lock.components).Count + ' 个文件 + ' + @($lock.bundles).Count + ' 个目录树')

    # ── 4. 全新 staging ──────────────────────────────────────────────────
    Step '[4/9] 克隆 HEAD 到全新 staging 目录（全部构建都在里面做，开发树一行不碰）'
    # A fresh build checkout remains inside the explicit output directory.
    $staging = Join-Path $outDir ('staging-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:staging = $staging
    if (Test-Path -LiteralPath $staging) { Fatal 'staging' ('staging 目录已存在（不该发生）：' + $staging) '删掉它或重跑' 2 }
    $cloneOut = @(Invoke-Native 'git' @('clone', '--quiet', '--no-hardlinks', $repoRoot, $staging))
    if ($script:lastExit -ne 0) { Fatal 'staging' ('git clone 失败（exit=' + $script:lastExit + '）：' + ($cloneOut -join ' / ')) '确认源仓库可读、仓库同级目录可写' 2 }
    Say ('  staging  : ' + $staging)
    $stageHead = @(Invoke-Native 'git' @('-C', $staging, 'rev-parse', 'HEAD'))
    if ($stageHead.Count -eq 0 -or $stageHead[0].Trim() -ne $headFull) {
        Fatal 'staging' ('克隆到的 commit（' + ($stageHead -join '') + '）与发布源 HEAD（' + $headFull + '）不一致') '发布期间仓库被改动（有人提交了）。重跑本脚本即可' 2
    }
    $stageDirty = @(Invoke-Native 'git' @('-C', $staging, 'status', '--porcelain=v1'))
    if ($stageDirty.Count -gt 0) { Fatal 'staging' 'staging 克隆出来却不干净（git clone 不该发生）' '检查源仓库是否有全局 hook/filter 改文件' 2 }
    Say '  已确认 staging 的 HEAD 与发布源一致，且工作区干净 OK'
    foreach ($document in @('README.md','使用说明.md','THIRD_PARTY_NOTICES.md','LICENSE')) {
        $publicDocument = Join-Path $staging ('release-docs\' + $document)
        if (Test-Path -LiteralPath $publicDocument) { Copy-Item -LiteralPath $publicDocument -Destination (Join-Path $staging $document) -Force }
    }
    $gitPrefix = (Join-Path $staging '.git') + [IO.Path]::DirectorySeparatorChar
    # repo: 资源必须来自本次干净检出的提交，不能从旧便携包覆盖回去。
    if ($script:srcPackages.ContainsKey('repo')) { $script:srcPackages['repo'] = $staging }

    # ── 5. 编译一次 + 完整离线回归 ───────────────────────────────────────
    Step '[5/9] 编译一次（Build-Hardened.ps1：编译 + 完整离线回归 + 自检）'
    # ⚠ 顺序很关键：这一步必须在**复制 bin\ 之前**做。
    #   理由（实测，2026-09-27）：启动器按「exe 旁边有没有 bin\mpv\mpv.exe」判断便携布局，
    #   而便携布局**刻意忽略命令行/配置文件的路径覆盖**（src\App\livehub-paths.cs 的 InitPaths：Portable 分支），
    #   于是离线回归里那条端到端的路径覆盖用例（tests\PathConfigTests.cs ④）在包布局下必然失败。
    #   离线回归是针对**开发布局**写的，所以先在开发布局下跑完整回归，再把依赖装进包 ——
    #   编译只做一次，回归、冒烟、打包用的是同一个 exe。
    $exePath = Join-Path $staging '看直播.exe'
    $offlineReportPath = Join-Path $outDir 'release-offline-report.json'
    $buildScript = Join-Path $staging 'Build-Hardened.ps1'
    if (-not (Test-Path -LiteralPath $buildScript)) { Fatal '构建' ('staging 里没有 Build-Hardened.ps1：' + $buildScript) '确认该文件已提交到仓库' 3 }
    Say ('  产物     : ' + $exePath + '   （本次唯一的启动器 exe；回归、冒烟、打包都用它）')
    $validationLayout = Join-Path $outDir 'build-validation'
    if (Test-Path -LiteralPath $validationLayout) { Fatal '构建' 'build-validation 目录已存在' '使用新的 -Out 目录，避免复用旧验收产物' 3 }
    & (Join-Path $staging 'tools\New-OfflineLayout.ps1') -Source $staging -Destination $validationLayout
    $validationExe = Join-Path $validationLayout '看直播.exe'
    Say '  布局     : 隔离的离线工具占位布局；不依赖作者机器安装，不运行真实播放器'
    $buildArguments = @('-NoProfile', '-File', $buildScript, '-Out', $validationExe, '-Report', $offlineReportPath)
    if ($Headless) { $buildArguments += '-Headless' }
    $buildOut = @(Invoke-Native 'pwsh' $buildArguments)
    $buildExit = $script:lastExit
    $buildLog = Join-Path $outDir 'build-output.log'
    $buildOut | Out-File -LiteralPath $buildLog -Encoding utf8
    $logLine = @($buildOut | Where-Object { $_ -match '离线测试日志： ' -or $_ -match '完整日志： ' } | Select-Object -Last 1)
    $passCount = @($buildOut | Where-Object { $_ -match '^PASS:' }).Count
    if ($buildExit -ne 0) {
        Show-TestFailure $buildOut
        Say '  构建输出（末尾 12 行）：'
        foreach ($l in @($buildOut | Select-Object -Last 12)) { Say ('    ' + $l) }
        Say ('  完整构建输出： ' + $buildLog)
        Fatal '构建/离线回归' ('Build-Hardened.ps1 退出码 ' + $buildExit + '（编译失败或离线回归有失败用例）') '看上面的第一条失败与失败用例，日志路径就打印在它旁边。修完重新提交再发布' 3
    }
    Copy-Item -LiteralPath $validationExe -Destination $exePath -Force
    Copy-Item -LiteralPath ($validationExe + '.config') -Destination ($exePath + '.config') -Force
    if ((Get-FileHash -LiteralPath ($exePath + '.config')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $staging 'app.config')).Hash) {
        Fatal '构建' '发布运行配置与源码 app.config 不一致' '检查构建产物和运行配置的复制路径' 3
    }
    Say ('  编译 + 离线回归 + 自检 全通过（exit=0，PASS 行 ' + $passCount + ' 条）')
    if ($logLine.Count -gt 0) { Say ('  ' + $logLine[0].Trim()) }
    $testsSummary = $null
    if (Test-Path -LiteralPath $offlineReportPath) {
        $report = Get-Content -LiteralPath $offlineReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $failedCases = @($report.tests | Where-Object { $_.status -ne 'passed' } | ForEach-Object { '[' + $_.category + '] ' + $_.name + ' — ' + $_.status + '：' + $_.message })
        $logRel = ''
        if ($logLine.Count -gt 0) { $logRel = $logLine[0].Trim().Replace('离线测试日志： ', '').Replace('完整日志： ', '') }
        $testsSummary = [ordered]@{
            passed = [int]$report.totals.passed
            failed = [int]$report.totals.failed
            skipped = [int]$report.totals.skipped
            checks = [int]$report.totals.checks
            durationMs = [int]$report.durationMs
            failedCases = $failedCases
            layout = 'isolated-offline（工具占位；真实依赖稍后按锁文件校验复制）'
        }
        Say ('  离线回归：通过 ' + $report.totals.passed + ' / 失败 ' + $report.totals.failed + ' / 跳过 ' + $report.totals.skipped + ' · 断言 ' + $report.totals.checks + ' 条 · ' + [math]::Round($report.durationMs / 1000, 1) + ' s')
    } else {
        Fatal '构建' '构建通过但没生成离线测试 JSON 报告（--report 没生效）' '检查 Build-Hardened.ps1 的 -Report 传参' 3
    }
    $selfTestFile = Join-Path $validationLayout 'runtime\selftest.txt'
    if (Test-Path -LiteralPath $selfTestFile) {
        $portableLine = @(Get-Content -LiteralPath $selfTestFile -Encoding UTF8 | Where-Object { $_ -match 'PORTABLE' } | Select-Object -First 1)
        if ($portableLine.Count -gt 0) { Say ('  自检     : ' + $portableLine[0].Trim()) }
    }

    # ── 6. 按清单复制依赖（逐个校验 sha256）→ staging 变成便携包布局 ──────
    Step '[6/9] 按 deps.lock.json 复制固定版本依赖（逐个校验 sha256，任何一个不符就失败）'
    $verified = New-Object System.Collections.Generic.List[object]
    foreach ($c in $lock.components) {
        # lite：不带播放器 —— mpv / PotPlayer 既不复制也不校验（其余组件照旧逐个校验 sha256）
        if (-not $IncludePotPlayer -and [string]$c.name -eq 'potplayer') {
            Say ('  · ' + ([string]$c.name).PadRight(12) + ' 跳过（PotPlayer 不再分发，用户自备）')
            continue
        }
        if ($Variant -eq 'lite' -and @('mpv', 'potplayer') -contains [string]$c.name) {
            Say ('  · ' + ([string]$c.name).PadRight(12) + ' 跳过（lite 变体不带播放器）')
            continue
        }
        $srcPath = Resolve-LockSource ([string]$c.source)
        $sha = Assert-FileMatchesLock $srcPath ([string]$c.sha256) ([long]$c.size) ([string]$c.name)
        $mirrorNote = ''
        if ($c.PSObject.Properties['mirrors']) {
            foreach ($m in @($c.mirrors)) {
                $mPath = Resolve-LockSource ([string]$m)
                [void](Assert-FileMatchesLock $mPath ([string]$c.sha256) ([long]$c.size) ([string]$c.name + '（镜像）'))
            }
            $mirrorNote = '（' + @($c.mirrors).Count + ' 个镜像源哈希一致）'
        }
        $relTarget = ([string]$c.target) -replace '/', '\'
        $dst = Join-Path $staging $relTarget
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
        if (-not [IO.Path]::GetFullPath($srcPath).Equals([IO.Path]::GetFullPath($dst), [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $srcPath -Destination $dst -Force
        }
        Say ('  · ' + ([string]$c.name).PadRight(12) + ' ' + ([string]$c.version).PadRight(26) + ' ' + ([string]$c.size).PadLeft(11) + ' B  sha256 ' + $sha.Substring(0, 12) + '...  OK ' + $mirrorNote + ' -> ' + $c.target)
        $verified.Add([pscustomobject]@{ name = [string]$c.name; target = [string]$c.target; version = [string]$c.version; sha256 = $sha.ToLowerInvariant(); size = [long]$c.size; license = [string]$c.license; source = [string]$c.source; kind = 'file' })
    }
    foreach ($b in $lock.bundles) {
        if (-not $IncludePotPlayer -and [string]$b.name -eq 'potplayer-dist') {
            Say ('  · 目录树 ' + ([string]$b.name).PadRight(14) + ' 跳过（PotPlayer 不再分发，用户自备）')
            continue
        }
        if ($Variant -eq 'lite' -and @('mpv-dist', 'potplayer-dist') -contains [string]$b.name) {
            Say ('  · 目录树 ' + ([string]$b.name).PadRight(14) + ' 跳过（lite 变体不带播放器）')
            continue
        }
        $srcDir = Resolve-LockSource ([string]$b.sourceDir)
        if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) { Fatal '依赖' ('锁定目录不存在：' + $srcDir) '绿色包目录被移动/改名了；改 deps.lock.json 的 sourcePackages' 5 }
        $excludes = @()
        if ($b.PSObject.Properties['exclude']) { foreach ($x in @($b.exclude)) { $excludes += (Convert-GlobToRegex ([string]$x)) } }
        $expected = @{}
        foreach ($f in @($b.files)) { $expected[([string]$f.path).ToLowerInvariant()] = $f }
        $actual = @{}
        $entries = New-Object System.Collections.Generic.List[object]
        $copyList = New-Object System.Collections.Generic.List[object]
        foreach ($f in @(Get-ChildItem -LiteralPath $srcDir -Recurse -File -Force)) {
            $rel = Get-RelPath $srcDir $f.FullName
            $skip = $false
            foreach ($rx in $excludes) { if ($rel -match $rx) { $skip = $true; break } }
            if ($skip) { continue }
            $h = Get-Sha256 $f.FullName
            $actual[$rel.ToLowerInvariant()] = [pscustomobject]@{ Path = $rel; Hash = $h; Size = [long]$f.Length }
            $entries.Add([pscustomobject]@{ Path = $rel; Hash = $h })
            $copyList.Add([pscustomobject]@{ Source = $f.FullName; Rel = $rel })
        }
        if ($actual.Count -ne $expected.Count) {
            $missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) })
            $extra = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) })
            $detail = '期望 ' + $expected.Count + ' 个文件，实际 ' + $actual.Count + ' 个'
            if ($missing.Count -gt 0) { $detail += '；清单里有而目录里没有：' + (($missing | Select-Object -First 5) -join ', ') }
            if ($extra.Count -gt 0) { $detail += '；目录里有而清单里没有：' + (($extra | Select-Object -First 5) -join ', ') }
            Fatal '依赖' ('目录树文件集合与清单不符（' + $b.name + '）：' + $detail) '源目录被增删过。确认无误后重新生成 deps.lock.json 并提交' 5
        }
        foreach ($key in $expected.Keys) {
            $e = $expected[$key]
            $a = $actual[$key]
            if ($a.Hash -ne ([string]$e.sha256).ToUpperInvariant()) {
                Fatal '依赖' ('目录树内文件 sha256 不符（' + $b.name + '）：' + $a.Path + ' 期望 ' + ([string]$e.sha256).ToUpperInvariant() + '，实际 ' + $a.Hash) '源文件被换过。确认无误后重新生成 deps.lock.json 并提交' 5
            }
            if ([long]$a.Size -ne [long]$e.size) {
                Fatal '依赖' ('目录树内文件大小不符（' + $b.name + '）：' + $a.Path) '源文件被换过。确认无误后重新生成 deps.lock.json 并提交' 5
            }
        }
        $sorted = @($entries | Sort-Object { $_.Path.ToLowerInvariant() })
        $treeHash = Get-TreeHash $sorted
        if ($treeHash -ne ([string]$b.treeSha256).ToLowerInvariant()) {
            Fatal '依赖' ('目录树哈希不符（' + $b.name + '）：期望 ' + $b.treeSha256 + '，实际 ' + $treeHash) '源目录整体被改动过。确认无误后重新生成 deps.lock.json 并提交' 5
        }
        $targetRel = ([string]$b.targetDir) -replace '/', '\'
        foreach ($item in $copyList) {
            $dst = Join-Path (Join-Path $staging $targetRel) ($item.Rel -replace '/', '\')
            $dstDir = Split-Path -Parent $dst
            if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
            Copy-Item -LiteralPath $item.Source -Destination $dst -Force
        }
        $totalBytes = 0
        foreach ($e in $sorted) { $totalBytes += [long]$expected[$e.Path.ToLowerInvariant()].size }
        $mb = [math]::Round($totalBytes / 1MB, 1)
        Say ('  · ' + ([string]$b.name).PadRight(12) + ' ' + ([string]$b.targetDir).PadRight(26) + ' ' + $actual.Count.ToString().PadLeft(4) + ' 个文件 ' + $mb.ToString().PadLeft(8) + ' MB  tree ' + $treeHash.Substring(0, 12) + '...  OK')
        $verified.Add([pscustomobject]@{ name = [string]$b.name; target = [string]$b.targetDir; version = '（目录树）'; sha256 = $treeHash; size = $totalBytes; license = '见包内 License/LGPL 等随附文件'; source = [string]$b.sourceDir; kind = 'bundle' })
    }
    # 依赖清单本身也放进包里：拿到包的人可以照着它复核包内二进制是不是锁定的那份。
    $lockInStaging = Join-Path $staging 'deps.lock.json'
    Copy-Item -LiteralPath $lockPath -Destination $lockInStaging -Force
    Say ('  · deps.lock.json 一并入包（' + (Get-Item -LiteralPath $lockInStaging).Length + ' 字节）')

    # 用户数据种子（data\）：**不做哈希锁定** —— 它本来就是会随使用变化的数据（收藏/历史/设置），
    # 锁死它只会让流水线天天误报。它的最终哈希照样进 manifest.artifacts，可追溯；
    # 里面的登录态由第 8 步的凭据扫描处理（默认剥离）。
    if ($lock.PSObject.Properties['seed']) {
        $seedCopied = 0
        $seedMissing = 0
        foreach ($s in @($lock.seed)) {
            $sSrc = Resolve-LockSource ([string]$s.source)
            $sRel = ([string]$s.target) -replace '/', '\'
            $sDst = Join-Path $staging $sRel
            if (-not (Test-Path -LiteralPath $sSrc -PathType Leaf)) {
                Say ('  · 种子缺失（跳过）：' + $s.source)
                $seedMissing++
                continue
            }
            $sDstDir = Split-Path -Parent $sDst
            if (-not (Test-Path -LiteralPath $sDstDir)) { New-Item -ItemType Directory -Force -Path $sDstDir | Out-Null }
            Copy-Item -LiteralPath $sSrc -Destination $sDst -Force
            $seedCopied++
            $sSize = (Get-Item -LiteralPath $sSrc).Length
            Say ('  · 种子     ' + $s.target + '  ' + $sSize + ' 字节（未锁定哈希：用户数据）')
        }
        Say ('  用户数据种子：复制 ' + $seedCopied + ' 个，缺失 ' + $seedMissing + ' 个（缺失不算失败）')
    }
    Say ('  依赖复制完成：' + $verified.Count + ' 个条目全部通过 sha256 校验 OK')
    Say '  staging 现在是便携包布局（bin\ + data\ + fonts\ + lua\），GUI 冒烟就在这个布局上跑'
    # 便携标记：lite 包没有 bin\mpv\mpv.exe，靠这个文件让程序仍按「便携布局」跑、
    # 并且知道「不带播放器是预期状态」。必须在自检/GUI 冒烟之前写好，否则它们看到的是开发布局。
    $markerPath = Join-Path $staging 'portable.marker'
    Set-Content -LiteralPath $markerPath -Value ('jubo-live ' + $Variant + ' portable package') -Encoding ascii
    $portableSelfTest = Start-Process -WindowStyle Hidden -FilePath $exePath -ArgumentList '--self-test' -Wait -PassThru
    if ($portableSelfTest.ExitCode -ne 0) { Fatal '便携自检' '真实依赖布局的离线自检失败' '检查 bin 与 lua 的必需文件' 3 }
    # lite：再证明一次「用户自带播放器」这条路 —— 用 --mpv= 指到锁定清单里的真 mpv，自检必须通过。
    if ($Variant -eq 'lite') {
        $mpvComponent = @($lock.components) | Where-Object { [string]$_.name -eq 'mpv' } | Select-Object -First 1
        if ($null -eq $mpvComponent) { Fatal '便携自检' 'lite 自检需要锁定清单里的 mpv 组件做阳性对照' '检查 deps.lock.json' 5 }
        $mpvSource = Resolve-LockSource ([string]$mpvComponent.source)
        if (-not (Test-Path -LiteralPath $mpvSource -PathType Leaf)) { Fatal '便携自检' ('阳性对照用的 mpv 不存在：' + $mpvSource) '检查 deps.lock.json 的 sourcePackages' 5 }
        $overrideTest = Start-Process -WindowStyle Hidden -FilePath $exePath -ArgumentList @('--self-test', ('--mpv=' + $mpvSource)) -Wait -PassThru
        if ($overrideTest.ExitCode -ne 0) { Fatal '便携自检' '--mpv= 指定播放器后自检仍失败（lite 的核心用法）' '检查便携布局下的命令行覆盖' 3 }
        Say ('  便携自检 : lite —— 不带播放器时通过；--mpv= 指定播放器后也通过（阳性对照 ' + (Split-Path -Leaf $mpvSource) + '）')
    }

    # ── 7. GUI 冒烟 ──────────────────────────────────────────────────────
    $guiInfo = [ordered]@{ ran = $false; exitCode = $null; summary = '（未运行）' }
    if ($SkipGuiSmoke) {
        Say ''
        Say '! -SkipGuiSmoke：跳过 GUI 冒烟（默认必跑；这会在 manifest.json 里被如实记成 ran=false）'
        $guiInfo = [ordered]@{ ran = $false; exitCode = $null; summary = '用户用 -SkipGuiSmoke 跳过'; skippedByUser = $true }
    } else {
        Step '[7/9] GUI 冒烟（在**最终便携包布局**上跑；Test-LauncherGui.ps1 只收自己拉起的 PID）'
        Say '  这一跑的是用户真正拿到的东西：exe 旁边有 bin\ → 便携布局，数据写进包内 data\ 与 runtime\'
        $guiScript = Join-Path $staging 'tests\Test-LauncherBackground.ps1'
        if (-not (Test-Path -LiteralPath $guiScript)) { Fatal 'GUI 冒烟' ('staging 里没有 GUI 冒烟脚本：' + $guiScript) '确认 tests\Test-LauncherGui.ps1 已提交' 4 }
        $guiOut = @(Invoke-Native 'pwsh' @('-NoProfile', '-File', $guiScript, '-Exe', $exePath, '-Output', (Join-Path $outDir 'launcher-controls.local.json')))
        $guiExit = $script:lastExit
        $guiLog = Join-Path $outDir 'gui-smoke.log'
        $guiOut | Out-File -LiteralPath $guiLog -Encoding utf8
        $guiNonEmpty = @($guiOut | Where-Object { $_.Trim() -ne '' })
        $guiSummary = '（无输出）'
        if ($guiNonEmpty.Count -gt 0) { $guiSummary = $guiNonEmpty[$guiNonEmpty.Count - 1].Trim() }
        $guiInfo = [ordered]@{ ran = $true; exitCode = $guiExit; summary = $guiSummary; mode = 'hidden-control-probe'; desktopShown = $false }
        if ($guiExit -ne 0) {
            Say ('  X GUI 冒烟失败（exit=' + $guiExit + '）：' + $guiSummary)
            Say '  关键输出：'
            foreach ($l in @($guiOut | Where-Object { $_ -match '缺少|重叠|窗口标题|找不到|提前退出' })) { Say ('    ' + $l) }
            Say ('  完整输出： ' + $guiLog)
            Fatal 'GUI 冒烟' ('退出码 ' + $guiExit + '：' + $guiSummary) '先手工跑一次同一条命令看界面；日志路径见上' 4
        }
        Say ('  GUI 冒烟通过：' + $guiSummary)
    }

    # ── 8. 凭据扫描 + 剥离 ───────────────────────────────────────────────
    Step '[8/9] 凭据扫描（cookie 文件 / 浏览器 profile / 结构化旁文件）'
    $credNamePatterns = @('*-cookies.txt', '*-cookies.txt.json', '*-login-profile', '*-verify-profile')
    $credDirPatterns = @('*-login-profile', '*-verify-profile')
    $credAll = New-Object System.Collections.Generic.List[object]
    foreach ($f in @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)) {
        if ($f.FullName.StartsWith($gitPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($pat in $credNamePatterns) {
            if ($f.Name -like $pat) { $credAll.Add([pscustomobject]@{ Kind = 'file'; Rel = (Get-RelPath $staging $f.FullName); Size = [long]$f.Length; Full = $f.FullName }); break }
        }
    }
    foreach ($d in @(Get-ChildItem -LiteralPath $staging -Recurse -Directory -Force)) {
        if ($d.FullName.StartsWith($gitPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($pat in $credDirPatterns) {
            if ($d.Name -like $pat) { $credAll.Add([pscustomobject]@{ Kind = 'dir'; Rel = (Get-RelPath $staging $d.FullName); Size = -1; Full = $d.FullName }); break }
        }
    }
    if ($credAll.Count -eq 0) {
        Say '  未发现 cookie 文件 / 浏览器 profile（staging 里没有登录态）'
    } else {
        foreach ($c in $credAll) {
            if ($c.Kind -eq 'dir') { $sizeText = '（目录）' } else { $sizeText = ('' + $c.Size + ' 字节') }
            Say ('  · 命中 ' + $c.Kind.PadRight(4) + ' ' + $c.Rel + '  ' + $sizeText)
        }
    }
    $strippedList = New-Object System.Collections.Generic.List[object]
    $keptList = New-Object System.Collections.Generic.List[object]
    if ($IncludeCredentials) {
        foreach ($c in $credAll) { $keptList.Add([ordered]@{ path = $c.Rel; kind = $c.Kind; size = $c.Size }) }
        if ($credAll.Count -gt 0) { Say ('  已按 -IncludeCredentials 保留 ' + $credAll.Count + ' 个（自用便携包；发给别人前请重新打包并去掉这个开关）') }
    } else {
        foreach ($c in $credAll) {
            Remove-Item -LiteralPath $c.Full -Recurse -Force -ErrorAction SilentlyContinue
            $strippedList.Add([ordered]@{ path = $c.Rel; kind = $c.Kind; size = $c.Size })
        }
        if ($credAll.Count -gt 0) { Say ('  已剥离 ' + $credAll.Count + ' 个凭据（默认行为，安全侧）') }
        # 复扫：剥离动作必须真的留下一个零命中的包，而不是「报告说剥了」就完事。
        $recheck = 0
        foreach ($f in @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)) {
            if ($f.FullName.StartsWith($gitPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            foreach ($pat in $credNamePatterns) { if ($f.Name -like $pat) { $recheck++; break } }
        }
        foreach ($d in @(Get-ChildItem -LiteralPath $staging -Recurse -Directory -Force)) {
            foreach ($pat in $credDirPatterns) { if ($d.Name -like $pat) { $recheck++; break } }
        }
        if ($recheck -ne 0) { Fatal '凭据' ('剥离后复扫仍命中 ' + $recheck + ' 个凭据对象') '检查是否有写回/重生成（例如自检又写了一次 cookie）' 3 }
        Say '  剥离后复扫：0 命中 OK'
    }

    # ── 9. manifest + ZIP ────────────────────────────────────────────────
    Step '[9/9] 生成 manifest.json 并打 ZIP'
    $builtAt = [DateTime]::UtcNow
    $builtAtIso = $builtAt.ToString('yyyy-MM-ddTHH:mm:ssZ')

    # ---- 打包内容裁剪：用**白名单**而不是黑名单 ----
    # 黑名单的问题是「明天开发树里多一个东西」时会悄悄进包；白名单是「没写进去就不进包」。
    $allowTopFiles = @('看直播.exe', '看直播.exe.config', '使用说明.md', 'emoji-sheet.png', 'emoji-sheet.txt', 'deps.lock.json', 'THIRD_PARTY_NOTICES.md', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'AntdUI.dll', 'portable.marker', 'LICENSE')
    $allowDirs = @('bin', 'lib', 'fonts', 'lua', 'data', 'licenses')
    $userDataFiles = @('data/bili-cookies.txt', 'data/bili-cookies.txt.json', 'data/favorites.json', 'data/history.json')
    $skippedSources = New-Object System.Collections.Generic.List[string]
    $skippedUserData = New-Object System.Collections.Generic.List[string]
    $allFiles = New-Object System.Collections.Generic.List[object]
    foreach ($f in @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)) {
        if ($f.FullName.StartsWith($gitPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $rel = Get-RelPath $staging $f.FullName
        $top = $rel.Split('/')[0]
        $inAllowedDir = ($allowDirs -contains $top)
        $isAllowedTopFile = ($allowTopFiles -contains $rel)
        if (-not $IncludeSources -and -not $inAllowedDir -and -not $isAllowedTopFile) {
            $skippedSources.Add($rel)
            continue
        }
        if (-not $IncludeUserData -and ($top -eq 'data' -or $userDataFiles -contains $rel)) {
            $skippedUserData.Add($rel)
            continue
        }
        $allFiles.Add($f)
    }
    $sortedFiles = @($allFiles | Sort-Object { (Get-RelPath $staging $_.FullName).ToLowerInvariant() })
    $artifacts = New-Object System.Collections.Generic.List[object]
    $artifactsBytes = 0
    foreach ($f in $sortedFiles) {
        $rel = Get-RelPath $staging $f.FullName
        if ($rel -eq 'manifest.json') { continue }   # 自指：manifest 不能把自己算进自己的 artifacts
        $h = Get-Sha256 $f.FullName
        $artifacts.Add([ordered]@{ path = $rel; sha256 = $h.ToLowerInvariant(); size = [long]$f.Length })
        $artifactsBytes += [long]$f.Length
    }

    $componentsOut = New-Object System.Collections.Generic.List[object]
    foreach ($v in $verified) {
        $componentsOut.Add([ordered]@{
            name = $v.name
            target = $v.target
            version = $v.version
            sha256 = $v.sha256
            size = $v.size
            license = $v.license
            source = $v.source
            kind = $v.kind
            verified = $true
        })
    }

    $manifest = [ordered]@{
        schema = 'livehub-release-manifest/1'
        buildId = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
        commit = [ordered]@{ full = $headFull; short = $headShort }
        dirty = $false
        dirtyNote = 'dirty=false 说的是打包内容：staging 是对 HEAD 的全新克隆，包内每个文件的 sha256 都登记在 artifacts 里'
        builtAt = $builtAtIso
        target = [ordered]@{ platform = 'windows'; architecture = 'x64'; framework = 'net48' }
        stagingHead = $stageHead[0].Trim()
        lockFile = [ordered]@{ path = 'deps.lock.json'; schema = [string]$lock.schema; generatedAt = $lockGeneratedAt }
        components = $componentsOut
        artifacts = $artifacts
        artifactsCount = $artifacts.Count
        artifactsBytes = $artifactsBytes
        manifestSelf = [ordered]@{ path = 'manifest.json'; note = 'artifacts 不含 manifest.json 自身（否则哈希自指）；manifest 就在包根 manifest.json' }
        tests = $testsSummary
        guiSmoke = $guiInfo
        onlinePlatforms = 'unverified'
        physicalMixedDpi = 'unverified'
        desktopRegressionMode = $(if ($Headless) { 'headless; native desktop cases explicitly skipped' } else { 'native desktop regression requested' })
        credentials = [ordered]@{ mode = $(if ($IncludeCredentials) { 'keep' } else { 'strip' }); stripped = $strippedList; kept = $keptList }
        # 打包裁剪（2026-09-27 定稿）：包内到底放了什么、裁掉了什么，manifest 里说清楚，
        # 免得拿到包的人以为「源码本来就没有」或者「作者忘了打进来」。
        packaging = [ordered]@{
            mode = $(if ($IncludeSources) { 'full' } else { 'runtime' })
            includeSources = [bool]$IncludeSources
            includeUserData = [bool]$IncludeUserData
            allowTopFiles = $allowTopFiles
            allowDirs = $allowDirs
            skippedDevFiles = $skippedSources.Count
            skippedUserDataFiles = $skippedUserData.Count
        }
        buildExitCode = $buildExit
        variant = $Variant
        releaseVersion = $releaseVersion
        includePotPlayer = [bool]$IncludePotPlayer
        portableMarker = 'portable.marker'
        pipeline = [ordered]@{ script = 'tools/Publish-Release.ps1'; schemaVersion = 1 }
    }
    $manifestPath = Join-Path $staging 'manifest.json'
    $manifestJson = ($manifest | ConvertTo-Json -Depth 12)
    Set-Content -LiteralPath $manifestPath -Value $manifestJson -Encoding utf8NoBOM
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $outDir 'manifest.json') -Force
    $manifestSha = (Get-Sha256 $manifestPath).ToLowerInvariant()
    Say ('  manifest : ' + $artifacts.Count + ' 个包内文件已登记，' + [math]::Round($artifactsBytes / 1MB, 1) + ' MB')
    if ($IncludeSources) { Say '  打包内容 : 全量（-IncludeSources：含源码/工具/文档/测试）' }
    else { Say ('  打包内容 : 可运行绿色包（白名单：' + ($allowDirs -join '、') + ' + ' + $allowTopFiles.Count + ' 个根文件）；已裁掉 ' + $skippedSources.Count + ' 个开发文件') }
    if ($IncludeUserData) { Say '  个人数据 : 保留（-IncludeUserData）' }
    elseif ($skippedUserData.Count -gt 0) { Say ('  个人数据 : 裁掉 ' + $skippedUserData.Count + ' 个（收藏/历史；要保留加 -IncludeUserData）') }

    # 命名规范（2026-10-16，照 GitHub Release 常见写法）：
    #   <项目>-<版本>-<平台>-<架构>-<变体>.zip   例：jubo-live-1.0.0-win-x64-lite.zip
    $zipName = 'jubo-live-' + $releaseVersion + '-win-x64-' + $Variant + '.zip'
    $zipPath = Join-Path $outDir $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force; Say ('  （覆盖已存在的 ' + $zipName + '）') }
    # ZIP 条目 = staging 里（除 .git）**所有**文件，含刚写好的 manifest.json —— 顺序 = 相对路径字典序，
    # 所以 manifest.json 在包内的位置是确定的（不是"碰巧排在最后"）。
    $zipEntries = @(@($sortedFiles) + @(Get-Item -LiteralPath $manifestPath) | Sort-Object { (Get-RelPath $staging $_.FullName).ToLowerInvariant() })
    # 条目顺序 = 相对路径字典序（manifest.json 也在其中，位置完全确定）；条目时间戳统一为 builtAt，
    # 免得 zip 字节里混进「打包那一刻的文件 mtime」这种不可复现的东西。
    $stamp = [DateTimeOffset]::new($builtAt)
    $fs = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($f in $zipEntries) {
                $name = Get-RelPath $staging $f.FullName
                # 两个实测踩过的坑：
                #   ① CreateEntryFromFile 是**扩展方法**（ZipFileExtensions），PowerShell 不能当实例方法调 → 必须静态调用；
                #   ② 时间戳必须在**打开条目写入之前**设：写成 CreateEntryFromFile 再赋 LastWriteTime，会报
                #      「Cannot modify entry in Create mode after entry has been opened for writing.」。
                # 所以这里手工 CreateEntry → 设时间戳 → 再写内容，语义等价且顺序合法。
                $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Fastest)
                $entry.LastWriteTime = $stamp
                $inStream = [IO.File]::OpenRead($f.FullName)
                try {
                    $outStream = $entry.Open()
                    try { $inStream.CopyTo($outStream) } finally { $outStream.Dispose() }
                } finally { $inStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $fs.Dispose() }
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    $zipSha = (Get-Sha256 $zipPath).ToLowerInvariant()
    $shaFile = $zipPath + '.sha256'
    # 格式：<小写 hex><两个空格><文件名>，与 sha256sum 的输出一致（可直接 sha256sum -c 校验）。
    Set-Content -LiteralPath $shaFile -Value ($zipSha + '  ' + $zipName) -Encoding ascii
    Say ('  ZIP      : ' + $zipPath + '  （' + [math]::Round($zipSize / 1MB, 1) + ' MB，' + $zipEntries.Count + ' 个条目）')
    Say ('  SHA-256  : ' + $zipSha)
    Say ('  校验文件 : ' + $shaFile + '（格式：<小写 hex> + 两个空格 + 文件名，与 sha256sum 兼容）')
    & (Join-Path $staging 'tools\Test-PublishPrivacy.ps1') -Archive $zipPath
    if (-not $?) { Fatal '隐私检查' '最终发布包未通过隐私检查' '修正公开内容后重新构建' 3 }
    Say ('  manifest sha256 : ' + $manifestSha)
    & (Join-Path $staging 'tools\Verify-ReleaseArchive.ps1') -Zip $zipPath -ExpectedCommit $headFull


    $script:summary = [ordered]@{
        outDir = $outDir
        zipPath = $zipPath
        zipName = $zipName
        zipSize = $zipSize
        zipSha = $zipSha
        shaFile = $shaFile
        manifestPath = (Join-Path $outDir 'manifest.json')
        manifestSha = $manifestSha
        components = $componentsOut
        tests = $testsSummary
        guiSmoke = $guiInfo
        stripped = $strippedList
        kept = $keptList
        headFull = $headFull
        headShort = $headShort
        logPath = $logPath
        buildLog = $buildLog
    }
} catch {
    $script:exitCode = 1
    Say ''
    Say '----------------------------------------------------------------'
    if ($script:failInfo) {
        Say ('X 失败于【' + $script:failInfo.Stage + '】：' + $script:failInfo.Message)
        Say ('  怎么办：' + $script:failInfo.Fix)
        $script:exitCode = $script:failInfo.Code
    } else {
        Say ('X 未预期的异常：' + $_.Exception.Message)
        Say '  怎么办：这是脚本没预料到的错误，请把下面这行调用栈连同上面的输出一起反馈'
        Say ('  ' + $_.ScriptStackTrace)
    }
    Say '  已中止：没有产出可发布的 ZIP（宁可不发，也不要发一个来路不清的包）'
    Say '----------------------------------------------------------------'
}

# ── 收尾：清理 staging（失败也清，除非 -KeepStaging）────────────────────
if ($script:staging -and (Test-Path -LiteralPath $script:staging)) {
    if ($KeepStaging) { Say ('  -KeepStaging：staging 保留在 ' + $script:staging) }
    else { Remove-Item -LiteralPath $script:staging -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($script:exitCode -eq 0 -and $script:summary) {
    $s = $script:summary
    $elapsed = (Get-Date) - $pipelineStart
    Say ''
    Say '=== 发布完成 ==='
    Say ('  commit      : ' + $s.headShort + '（完整 ' + $s.headFull + '）')
    Say ('  ZIP         : ' + $s.zipPath + '   （' + [math]::Round($s.zipSize / 1MB, 1) + ' MB）')
    Say ('  SHA-256     : ' + $s.zipSha + '   —— 写入 ' + (Split-Path -Leaf $s.shaFile))
    Say ('  manifest    : ' + $s.manifestPath + '   （sha256 ' + $s.manifestSha.Substring(0, 16) + '...）')
    Say ('  离线回归    : 通过 ' + $s.tests.passed + ' / 失败 ' + $s.tests.failed + ' / 跳过 ' + $s.tests.skipped + ' · 断言 ' + $s.tests.checks + ' 条 · ' + [math]::Round($s.tests.durationMs / 1000, 1) + ' s')
    if ($s.guiSmoke.ran) { Say ('  GUI 冒烟    : 通过（exit=0）—— ' + $s.guiSmoke.summary) }
    else { Say ('  GUI 冒烟    : 未运行（' + $s.guiSmoke.summary + '）') }
    # ⚠ 别写 @($s.components)：部分 PowerShell 版本对 List[object] 用数组子表达式 @() 会抛
    #   「Argument types do not match」（List[string]/List[int]/ArrayList/数组都正常，只有 List[object] 中招）。
    #   直接取 .Count 即可。
    Say ('  依赖组件    : ' + $s.components.Count + ' 个条目全部 sha256 校验通过')
    Say ('  凭据        : 剥离 ' + $s.stripped.Count + ' 个 / 保留 ' + $s.kept.Count + ' 个')
    Say ('  发布日志    : ' + $s.logPath)
    Say ('  构建输出    : ' + $s.buildLog)
    Say ('  总耗时      : ' + [math]::Round($elapsed.TotalSeconds, 1) + ' s')
} elseif ($script:exitCode -ne 0) {
    Say ''
    Say ('=== 发布失败（退出码 ' + $script:exitCode + '）—— 没有产出 ZIP ===')
}

try { Stop-Transcript | Out-Null } catch { }
exit $script:exitCode
