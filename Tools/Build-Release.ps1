<#
.SYNOPSIS
    RimFacilityCompat 打包脚本：在一次运行中同时产出 Release 包与 Debug 包。

.DESCRIPTION
    版本号唯一来源为 About/About.xml 的 <modVersion>，脚本负责：
      1. 校验工作区状态与 tag 是否已存在（仅告警，不阻塞）；
      2. 分别以 Release / Debug 配置编译（输出目录已由 csproj 隔离）；
      3. 按发布目录白名单组装两个包（排除 Source / Logs / Spec 等）；
      4. Debug 包把包内 <modVersion> 改写为 "<版本>-debug"，便于与发行版区分；
      5. 压缩为 dist/ 下的两个 zip。

.PARAMETER CreateRelease
    打包完成后调用 gh CLI 创建 GitHub Release 并上传两个 zip（需已安装并登录 gh）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\Build-Release.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\Build-Release.ps1 -CreateRelease

.NOTES
    详见 Spec/发布流程.md
#>
[CmdletBinding()]
param(
    [switch]$CreateRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ==== 可配置项 ====
$PackageName    = 'RimFacilityCompat'                    # 发布包顶层文件夹名（zip 解压后的目录名）
$AssemblyName   = 'FacilityCompat'                       # 编译产物 dll 名（取自 csproj 项目名）
$IncludeDirs    = @('About', 'Languages', 'Presets')     # 进入发布包的目录白名单（相对 mod 根）
$GameVersionDir = '1.6'                                  # 游戏版本目录
$DistDirName    = 'dist'                                 # 打包输出目录（已被 .gitignore 忽略）
$DebugModSuffix = 'debug'                                # 调试包版本号与文件名后缀
# ==================

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot "$GameVersionDir\Source\$AssemblyName.csproj"
$AboutPath  = Join-Path $RepoRoot 'About\About.xml'
$DistDir    = Join-Path $RepoRoot $DistDirName
$StageDir   = Join-Path $DistDir 'stage'

<#
.SYNOPSIS 输出阶段提示。
#>
function Write-Step {
    param([string]$Text)
    Write-Host "==> $Text" -ForegroundColor Cyan
}

<#
.SYNOPSIS 从 About.xml 读取 <modVersion>。
.PARAMETER Path About.xml 的绝对路径。
#>
function Get-ModVersion {
    param([string]$Path)

    if (-not (Test-Path $Path)) { throw "未找到 About.xml：$Path" }

    [xml]$xml = Get-Content -Path $Path -Raw
    $version = $xml.ModMetaData.modVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "About.xml 中缺少 <modVersion>，请先补全后再打包"
    }
    return $version.Trim()
}

<#
.SYNOPSIS 以指定配置编译工程。
.PARAMETER Configuration 编译配置（Release / Debug）。
#>
function Invoke-Build {
    param([string]$Configuration)

    Write-Step "编译 $Configuration 配置"
    & dotnet build $CsprojPath -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$Configuration 编译失败（exit $LASTEXITCODE）" }
}

<#
.SYNOPSIS 按白名单组装单个发布包目录。
.PARAMETER ModRoot 目标包根目录（即包内顶层文件夹）。
.PARAMETER DllPath 要放入 1.6/Assemblies 的 dll 路径。
.PARAMETER IsDebug 是否为调试包（决定是否改写 modVersion）。
.PARAMETER Version 版本号（用于调试包改写）。
#>
function Copy-PackageLayout {
    param(
        [string]$ModRoot,
        [string]$DllPath,
        [bool]$IsDebug,
        [string]$Version
    )

    if (-not (Test-Path $DllPath)) { throw "未找到编译产物：$DllPath" }

    if (Test-Path $ModRoot) { Remove-Item $ModRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $ModRoot -Force | Out-Null

    # 白名单目录整体复制（About / Languages / Presets）
    foreach ($dir in $IncludeDirs) {
        $src = Join-Path $RepoRoot $dir
        if (-not (Test-Path $src)) { throw "发布包必需目录缺失：$dir" }
        Copy-Item -Path $src -Destination (Join-Path $ModRoot $dir) -Recurse -Force
    }

    # 编译产物放入版本目录下的 Assemblies
    $asmDir = Join-Path $ModRoot "$GameVersionDir\Assemblies"
    New-Item -ItemType Directory -Path $asmDir -Force | Out-Null
    Copy-Item -Path $DllPath -Destination $asmDir -Force

    # 调试包：改写包内版本号，便于玩家在游戏内 mod 列表区分（不改动仓库原文件）
    if ($IsDebug) {
        $targetAbout = Join-Path $ModRoot 'About\About.xml'
        $text = [System.IO.File]::ReadAllText($targetAbout)
        $text = [regex]::Replace($text, '<modVersion>.*?</modVersion>', "<modVersion>$Version-$DebugModSuffix</modVersion>")
        [System.IO.File]::WriteAllText($targetAbout, $text, (New-Object System.Text.UTF8Encoding $true))
    }
}

<#
.SYNOPSIS 将目录压缩为 zip（保留顶层文件夹，显式 UTF-8 条目名以兼容中文文件名）。
.PARAMETER SourceDir 待压缩目录（其父目录作为 zip 内路径基准）。
.PARAMETER DestZip 目标 zip 路径。
#>
function New-ZipFromDirectory {
    param([string]$SourceDir, [string]$DestZip)

    Add-Type -AssemblyName System.IO.Compression | Out-Null

    if (Test-Path $DestZip) { Remove-Item $DestZip -Force }
    New-Item -ItemType Directory -Path (Split-Path -Parent $DestZip) -Force | Out-Null

    $baseDir = Split-Path -Parent $SourceDir
    $fileStream = [System.IO.File]::Open($DestZip, [System.IO.FileMode]::Create)
    $archive = New-Object -TypeName System.IO.Compression.ZipArchive -ArgumentList $fileStream, ([System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -Path $SourceDir -Recurse -File) {
            $entryName = $file.FullName.Substring($baseDir.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $inStream = [System.IO.File]::OpenRead($file.FullName)
                try { $inStream.CopyTo($entryStream) } finally { $inStream.Dispose() }
            } finally {
                $entryStream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

# ================== 主流程 ==================

$version = Get-ModVersion -Path $AboutPath
Write-Step "版本号：$version"

# 工作区未提交改动会导致包内容不对应任何 commit，仅告警
$dirty = & git -C $RepoRoot status --porcelain
if ($dirty) {
    Write-Warning "工作区存在未提交改动，Debug/Release 包可能不对应任何 commit：`n$dirty"
}

# 同名 tag 已存在通常意味着忘记提升版本号
$existingTag = & git -C $RepoRoot tag -l "v$version"
if ($existingTag) {
    Write-Warning "tag v$version 已存在，请确认是否已提升 <modVersion>"
}

Invoke-Build 'Release'
Invoke-Build 'Debug'

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

$releaseModRoot = Join-Path $StageDir "release\$PackageName"
$debugModRoot   = Join-Path $StageDir "$DebugModSuffix\$PackageName"

Copy-PackageLayout -ModRoot $releaseModRoot `
    -DllPath (Join-Path $RepoRoot "$GameVersionDir\Assemblies\$AssemblyName.dll") `
    -IsDebug $false -Version $version

Copy-PackageLayout -ModRoot $debugModRoot `
    -DllPath (Join-Path $RepoRoot "$GameVersionDir\AssembliesDebug\$AssemblyName.dll") `
    -IsDebug $true -Version $version

$releaseZip = Join-Path $DistDir "$PackageName-$version.zip"
$debugZip   = Join-Path $DistDir "$PackageName-$version-$DebugModSuffix.zip"

New-ZipFromDirectory -SourceDir $releaseModRoot -DestZip $releaseZip
New-ZipFromDirectory -SourceDir $debugModRoot -DestZip $debugZip

Write-Step "打包完成"
Write-Host "  Release 包：$releaseZip"
Write-Host "  Debug   包：$debugZip"
Write-Host "  组装目录  ：$StageDir"

if ($CreateRelease) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "未检测到 gh CLI，跳过自动发布。请手动在 GitHub 创建 Release 并上传上述两个 zip。"
    } else {
        Write-Step "创建 GitHub Release v$version"
        & gh release create "v$version" $releaseZip $debugZip --title "v$version" --generate-notes
        if ($LASTEXITCODE -ne 0) { throw "gh release create 失败（exit $LASTEXITCODE）" }
    }
}
