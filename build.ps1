[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$NoBootstrap
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$toolsDirectory = Join-Path $projectRoot '.tools'
$sdkDirectory = Join-Path $toolsDirectory 'dotnet'
$dotnetExecutable = Join-Path $sdkDirectory 'dotnet.exe'
$projectFile = Join-Path $projectRoot 'src\UsbBatchTool\UsbBatchTool.csproj'
$publishDirectory = Join-Path $projectRoot 'dist'
$applicationName = 'USB批量烧录工具'

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "找不到项目文件：$projectFile"
}

$usingProjectSdk = Test-Path -LiteralPath $dotnetExecutable -PathType Leaf
if (-not $usingProjectSdk) {
    # Contributors who already have a compatible SDK should not have to
    # download another copy into the checkout.
    $installedDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($installedDotnet) {
        $installedSdks = @(& $installedDotnet.Source --list-sdks)
        if ($LASTEXITCODE -eq 0 -and @($installedSdks | Where-Object {
            $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8
        }).Count -gt 0) {
            $dotnetExecutable = $installedDotnet.Source
        }
    }
}

if (-not (Test-Path -LiteralPath $dotnetExecutable -PathType Leaf)) {
    if ($NoBootstrap) {
        throw "项目本地 .NET SDK 不存在：$dotnetExecutable。请取消 -NoBootstrap 以下载官方 SDK。"
    }

    Write-Host '正在从微软官方查询 .NET 8 SDK，并下载到项目 .tools 目录……'
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $releaseMetadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
    $releaseMetadata = Invoke-RestMethod -Uri $releaseMetadataUrl -TimeoutSec 60
    $sdkVersion = $releaseMetadata.'latest-sdk'
    if ($sdkVersion -notmatch '^8\.0\.\d+$') {
        throw "元数据返回了无效 .NET 8 SDK 版本：$sdkVersion"
    }
    $sdkReleases = @($releaseMetadata.releases | ForEach-Object { $_.sdks } |
        Where-Object { $_.version -eq $sdkVersion })
    if ($sdkReleases.Count -eq 0) {
        throw '微软官方版本元数据中未找到最新 .NET 8 SDK。'
    }
    $sdkRelease = $sdkReleases[0]

    $sdkAssets = @($sdkRelease.files |
        Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' })
    if ($sdkAssets.Count -eq 0) {
        throw '微软官方版本元数据中未找到 Windows x64 SDK ZIP 及 SHA-512。'
    }
    $sdkAsset = $sdkAssets[0]
    if ($sdkAsset.hash -notmatch '^[a-fA-F0-9]{128}$' -or -not $sdkAsset.url) {
        throw '微软官方 SDK 元数据缺少有效 SHA-512 或下载地址。'
    }

    $sdkUri = [Uri]$sdkAsset.url
    if ($sdkUri.Scheme -ne 'https' -or $sdkUri.DnsSafeHost -notin @(
        'builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com',
        'dotnetcli.azureedge.net', 'ci.dot.net'
    )) {
        throw "SDK 下载地址不属于预期官方 HTTPS 主机：$sdkUri"
    }

    $sdkArchive = Join-Path $toolsDirectory "dotnet-sdk-$sdkVersion-win-x64.zip"
    if (-not (Test-Path -LiteralPath $sdkArchive -PathType Leaf)) {
        Invoke-WebRequest -Uri $sdkUri.AbsoluteUri -OutFile $sdkArchive -TimeoutSec 900
    }
    $actualHash = (Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash
    if ($actualHash -ine $sdkAsset.hash) {
        throw "SDK SHA-512 校验失败，未解压。请删除该下载文件后重试：$sdkArchive"
    }

    Write-Host "SHA-512 校验通过，正在解压 .NET SDK $sdkVersion……"
    New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkDirectory -Force
    $usingProjectSdk = $true
}

if ($usingProjectSdk) {
    $env:DOTNET_ROOT = $sdkDirectory
}
$env:DOTNET_CLI_HOME = Join-Path $toolsDirectory 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $toolsDirectory 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

Write-Host '正在检查项目本地 SDK……'
& $dotnetExecutable --version
if ($LASTEXITCODE -ne 0) {
    throw "本地 .NET SDK 启动失败，退出代码：$LASTEXITCODE"
}

Write-Host '正在生成 Windows x64 自包含单文件 EXE……'
& $dotnetExecutable publish $projectFile `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:EnableCompressionInSingleFile=true' `
    '-p:PublishTrimmed=false' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false' `
    "-p:AssemblyName=$applicationName"

if ($LASTEXITCODE -ne 0) {
    throw "发布失败，退出代码：$LASTEXITCODE"
}

$publishedExecutable = Join-Path $publishDirectory "$applicationName.exe"
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "发布完成但没有找到预期 EXE：$publishedExecutable"
}

Write-Host ''
Write-Host "构建完成：$publishedExecutable" -ForegroundColor Green
Write-Host '此 EXE 自带 .NET 运行时，目标电脑无需安装开发环境。'
