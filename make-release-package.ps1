[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.2',
    [string]$SourceDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $root "artifacts\release-build-v$Version"
    & (Join-Path $root 'build-offline.ps1') -PackageRuntime -OutputDirectory $SourceDirectory
}
$offline = [IO.Path]::GetFullPath($SourceDirectory)
$releaseRoot = Join-Path $root 'dist\release'
$package = Join-Path $releaseRoot "USB批量烧录工具-v$Version"
$zip = Join-Path $releaseRoot "USB批量烧录工具-v$Version-win-x64.zip"
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $zip)) {
    throw "发布目标已存在，未覆盖。请使用新的版本号：$package"
}
foreach ($name in @('USB批量烧录工具.exe','UsbBatchTool.Offline.dll','launch.ps1','runtime\pwsh.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $offline $name) -PathType Leaf)) { throw "缺少运行文件：$name" }
}
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach ($name in @('USB批量烧录工具.exe','UsbBatchTool.Offline.dll','launch.ps1','runtime')) {
    Copy-Item -LiteralPath (Join-Path $offline $name) -Destination (Join-Path $package $name) -Recurse -Force
}
$readme = @'
USB 批量烧录工具 VERSION

使用方法：
1. 解压整个文件夹，不要只提取 EXE。
2. 双击“USB批量烧录工具.exe”，并按 Windows 提示授予管理员权限。
3. 首次启动会在程序目录自动创建 config、cache、logs 文件夹。
4. 原始镜像写入会清空目标 U 盘，请确认磁盘和容量。

请保留 runtime 文件夹、launch.ps1 和 UsbBatchTool.Offline.dll，它们是程序运行所需文件。
'@
Set-Content -LiteralPath (Join-Path $package '使用说明.txt') -Encoding UTF8 -Value ($readme.Replace('VERSION', "v$Version"))
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $package 'LICENSE')
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$files = Get-ChildItem -LiteralPath $package -Recurse -File
[PSCustomObject]@{
    Package = $package
    Zip = $zip
    ZipMB = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
    ExpandedMB = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
    Files = $files.Count
    HasConfig = Test-Path (Join-Path $package 'config')
    HasCache = Test-Path (Join-Path $package 'cache')
    HasLogs = Test-Path (Join-Path $package 'logs')
} | Format-List
