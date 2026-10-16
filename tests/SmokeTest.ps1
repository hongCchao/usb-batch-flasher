[CmdletBinding()]
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\dist\offline\UsbBatchTool.Offline.dll'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\ui-demo.png'),
    [int]$RenderWidth = 1300,
    [int]$RenderHeight = 860,
    [switch]$TestCopy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw '此离屏 WPF 测试需要 STA，请通过 pwsh -NoProfile -STA -File tests\SmokeTest.ps1 执行。'
}

$resolvedAssembly = [IO.Path]::GetFullPath($AssemblyPath)
if (-not [IO.File]::Exists($resolvedAssembly)) { throw "找不到程序集：$resolvedAssembly" }
if (-not $TestCopy) {
    $scratchParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\test-data'))
    [IO.Directory]::CreateDirectory($scratchParent) | Out-Null
    $scratchRoot = Join-Path $scratchParent ('ui-tests-' + [Guid]::NewGuid().ToString('N'))
    if ([IO.Directory]::Exists($scratchRoot) -or [IO.File]::Exists($scratchRoot)) { throw "测试目录已存在：$scratchRoot" }
    [IO.Directory]::CreateDirectory($scratchRoot) | Out-Null
    try {
        $testAssembly = Join-Path $scratchRoot ([IO.Path]::GetFileName($resolvedAssembly))
        [IO.File]::Copy($resolvedAssembly, $testAssembly)
        $pwshExe = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        & $pwshExe -NoProfile -STA -File $PSCommandPath -AssemblyPath $testAssembly -OutputPath $OutputPath -RenderWidth $RenderWidth -RenderHeight $RenderHeight -TestCopy
        if ($LASTEXITCODE -ne 0) { throw "离屏测试子进程失败，退出码 $LASTEXITCODE。" }
    }
    finally {
        $full = [IO.Path]::GetFullPath($scratchRoot)
        $parent = [IO.Path]::GetFullPath($scratchParent).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($full), $parent, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($full).StartsWith('ui-tests-', [StringComparison]::Ordinal) -or
            ([IO.File]::GetAttributes($full) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "测试目录验证失败，未执行清理：$full"
        }
        [IO.Directory]::Delete($full, $true)
    }
    return
}

$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
$expectedBase = [IO.Path]::GetDirectoryName($resolvedAssembly).TrimEnd([IO.Path]::DirectorySeparatorChar)
$actualBase = [IO.Path]::GetFullPath([UsbBatchTool.Services.AppPaths]::Base).TrimEnd([IO.Path]::DirectorySeparatorChar)
$allowedParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\test-data')).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not [string]::Equals($actualBase, $expectedBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([IO.Path]::GetDirectoryName($actualBase), $allowedParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($actualBase).StartsWith('ui-tests-', [StringComparison]::Ordinal)) {
    throw "离屏测试隔离失败：AppPaths.Base=$actualBase"
}

# The offline build must embed the current artwork from the workspace, including
# the failure state. Comparing content catches stale resources after a rebuild.
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$statusAssets = @{
    'connected.png' = '已插入.png'
    'disconnected.png' = '未插入.png'
    'failed.png' = '烧录失败.png'
}
foreach ($resourceName in $statusAssets.Keys) {
    $sourcePath = Join-Path $projectRoot $statusAssets[$resourceName]
    $stream = $assembly.GetManifestResourceStream("UsbBatchTool.Assets.$resourceName")
    if ($null -eq $stream) { throw "离线程序缺少状态素材：$resourceName" }
    try { $embeddedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($sourcePath)))
    if ($embeddedHash -ne $sourceHash) { throw "离线程序中的状态素材不是工作目录最新文件：$($statusAssets[$resourceName])" }
}

function Get-ImagePixelsHash([Windows.Media.Imaging.BitmapSource]$image) {
    $stride = [int][Math]::Ceiling($image.PixelWidth * $image.Format.BitsPerPixel / 8)
    $pixels = [byte[]]::new($stride * $image.PixelHeight)
    $image.CopyPixels($pixels, $stride, 0)
    return "$($image.PixelWidth)x$($image.PixelHeight):$($image.Format):$([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels)))"
}

$statusBinding = [UsbBatchTool.PortBinding]::new()
$statusBinding.LocationPaths.Add('USBROOT(0)#USB(1)')
$statusPort = [UsbBatchTool.PortViewModel]::new($statusBinding)
$stateChanges = [Collections.Generic.List[string]]::new()
$stateHandler = [ComponentModel.PropertyChangedEventHandler]{
    param($sender, $eventArgs)
    $stateChanges.Add($eventArgs.PropertyName) | Out-Null
}
$statusPort.add_PropertyChanged($stateHandler)
$disconnectedHash = Get-ImagePixelsHash ([UsbBatchTool.EmbeddedAssets]::Image('disconnected.png'))
$connectedHash = Get-ImagePixelsHash ([UsbBatchTool.EmbeddedAssets]::Image('connected.png'))
$failedHash = Get-ImagePixelsHash ([UsbBatchTool.EmbeddedAssets]::Image('failed.png'))
if ($connectedHash -eq $disconnectedHash -or $connectedHash -eq $failedHash -or $disconnectedHash -eq $failedHash) {
    throw '三种端口状态素材的实际画面重复。'
}
if ((Get-ImagePixelsHash $statusPort.StateImage) -ne $disconnectedHash) { throw '空端口未显示“未插入”素材。' }
$connectedDisk = [UsbBatchTool.UsbDisk]::new()
$connectedDisk.Number = 123
$connectedDisk.Model = 'Synthetic USB'
$connectedDisk.SerialNumber = 'NO-PHYSICAL-DEVICE'
$connectedDisk.PnpDeviceId = 'TEST-ONLY'
$connectedDisk.LocationPaths.Add('USBROOT(0)#USB(1)')
$connectedDisk.SizeBytes = 1000000000
$connectedDisk.BusType = 7
$connectedDisk.IsRemovable = $true
$connectedDisk.SafetyVerified = $true
$statusPort.Disk = $connectedDisk
if ((Get-ImagePixelsHash $statusPort.StateImage) -ne $connectedHash) { throw '插入设备后未显示“已插入”素材。' }
$statusPort.HasWriteFailure = $true
if ((Get-ImagePixelsHash $statusPort.StateImage) -ne $failedHash) { throw '烧录失败后未显示“烧录失败”素材。' }
if ($statusPort.StateText -ne '烧录失败') { throw '烧录失败后端口文字状态未更新。' }
if (-not $stateChanges.Contains('StateImage') -or -not $stateChanges.Contains('StateText')) {
    throw '烧录失败后未通知界面更新端口图像与文字。'
}
$sameDisk = [UsbBatchTool.UsbDisk]::new()
$sameDisk.Number = $connectedDisk.Number
$sameDisk.Model = $connectedDisk.Model
$sameDisk.SerialNumber = $connectedDisk.SerialNumber
$sameDisk.PnpDeviceId = $connectedDisk.PnpDeviceId
$sameDisk.LocationPaths.Add('USBROOT(0)#USB(1)')
$sameDisk.SizeBytes = $connectedDisk.SizeBytes
$sameDisk.BusType = 7
$sameDisk.IsRemovable = $true
$sameDisk.SafetyVerified = $true
$statusPort.Disk = $sameDisk
if (-not $statusPort.HasWriteFailure -or (Get-ImagePixelsHash $statusPort.StateImage) -ne $failedHash) {
    throw '设备未拔出时重复扫描不应清除烧录失败状态。'
}
$statusPort.Disk = $null
if ($statusPort.HasWriteFailure -or (Get-ImagePixelsHash $statusPort.StateImage) -ne $disconnectedHash -or $statusPort.StateText -ne '未插入') {
    throw '拔出设备后端口未清除失败并刷新为“未插入”。'
}
$statusPort.Disk = $connectedDisk
if ((Get-ImagePixelsHash $statusPort.StateImage) -ne $connectedHash -or $statusPort.StateText -ne '已插入') {
    throw '重新插入设备后端口未刷新为“已插入”。'
}
$statusPort.remove_PropertyChanged($stateHandler)

$store = [UsbBatchTool.Services.SettingsStore]::new()
$fresh = $store.Load()
if ($fresh.Ports.Count -ne 0) { throw '全新配置下默认端口数量不是 0。' }

$app = [UsbBatchTool.App]::new()
$app.InitializeComponent()
$emptyWindow = [UsbBatchTool.MainWindow]::new($true)
$windowIcon = [Windows.Media.Imaging.BitmapSource]$emptyWindow.Icon
if ($windowIcon.PixelWidth -lt 48 -or $windowIcon.PixelHeight -lt 48) {
    throw "任务栏图标仍使用小尺寸：$($windowIcon.PixelWidth)×$($windowIcon.PixelHeight)"
}
$emptyLayout = [Windows.FrameworkElement]$emptyWindow.Content
if ($emptyLayout.FindName('PortsItems').Items.Count -ne 0) { throw '默认 0 口时仍生成端口卡片。' }
$emptyState = $emptyLayout.FindName('UnboundText')
if ($emptyState.Visibility -ne [Windows.Visibility]::Visible -or [string]::IsNullOrWhiteSpace($emptyState.Text)) {
    throw '默认 0 口时没有显示添加端口引导。'
}
$emptyWindow.Content = $null

# Exercise the actual settings editor methods on an unshown WPF window.
$settingsType = [UsbBatchTool.Views.PortSettingsWindow]
$settingsFlags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance
$editor = [UsbBatchTool.Views.PortSettingsWindow]::new([UsbBatchTool.AppSettings]::new(), [UsbBatchTool.UsbDisk[]]@())
$workingField = $settingsType.GetField('_working', $settingsFlags)
$listField = $settingsType.GetField('_portsList', $settingsFlags)
$countField = $settingsType.GetField('_portCountBox', $settingsFlags)
$add = $settingsType.GetMethod('AddPort', $settingsFlags)
$move = $settingsType.GetMethod('MovePort', $settingsFlags)
$remove = $settingsType.GetMethod('RemoveSelectedPort', $settingsFlags)
$working = $workingField.GetValue($editor)
$portList = $listField.GetValue($editor)
$countBox = $countField.GetValue($editor)
if ($working.Ports.Count -ne 0 -or $portList.Items.Count -ne 0 -or $countBox.Text -ne '0') {
    throw '设置窗口空配置未保持 0 口。'
}
for ($i = 0; $i -lt 3; $i++) { $add.Invoke($editor, @($null, $null)) | Out-Null }
if ($working.Ports.Count -ne 3 -or $countBox.Text -ne '3') { throw '新增 3 个端口后数量未同步。' }
$ids = @($working.Ports | ForEach-Object Id)
$portList.SelectedIndex = 2
$move.Invoke($editor, @(-1)) | Out-Null
if ($working.Ports[1].Id -ne $ids[2] -or $working.Ports[1].Order -ne 2 -or
    $working.Ports[2].Id -ne $ids[1] -or $working.Ports[2].Order -ne 3) {
    throw '上移端口后顺序或绑定身份不正确。'
}
$portList.SelectedIndex = 1
$remove.Invoke($editor, @()) | Out-Null
if ($working.Ports.Count -ne 2 -or $working.Ports[0].Id -ne $ids[0] -or
    $working.Ports[1].Id -ne $ids[1] -or $working.Ports[1].Order -ne 2 -or
    $countBox.Text -ne '2') {
    throw '删除中间端口后数量、顺序或剩余身份不正确。'
}

# Build the seven-card demo from synthetic settings in this isolated directory.
$demoSettings = [UsbBatchTool.AppSettings]::new()
for ($i = 1; $i -le 7; $i++) {
    $port = [UsbBatchTool.PortBinding]::new()
    $port.Order = $i
    $port.Name = "演示端口 $i"
    $demoSettings.Ports.Add($port)
}
$store.Save($demoSettings)
$window = [UsbBatchTool.MainWindow]::new($true)

# Populate demo models without opening a native window or scanning hardware.
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance
$refresh = $window.GetType().GetMethod('RefreshDevicesAsync', $flags)
$refreshTask = $refresh.Invoke($window, @($false))
$refreshTask.GetAwaiter().GetResult() | Out-Null

$layout = [Windows.FrameworkElement]$window.Content
$layout.FindName('AccessText').Text = '演示模式 · 禁止写入'
$window.Content = $null
$width = $RenderWidth
$height = $RenderHeight
$canvas = [Windows.Controls.Border]::new()
$canvas.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#F4F7FC')
[Windows.Documents.TextElement]::SetFontFamily($canvas, [Windows.Media.FontFamily]::new('Microsoft YaHei UI, Segoe UI'))
[Windows.Documents.TextElement]::SetFontSize($canvas, 13)
[Windows.Documents.TextElement]::SetForeground($canvas, [Windows.Media.BrushConverter]::new().ConvertFromString('#172B4D'))
$canvas.Child = $layout
$canvas.Measure([Windows.Size]::new($width, $height))
$canvas.Arrange([Windows.Rect]::new(0, 0, $width, $height))
$canvas.UpdateLayout()

$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
    $width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($canvas)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$stream = [IO.File]::Create($resolvedOutput)
try { $encoder.Save($stream) }
finally { $stream.Dispose() }

if ($layout.FindName('PortsItems').Items.Count -ne 7) { throw '演示配置端口数量不是 7。' }
$renderedPortCount = @($layout.FindName('PortsItems').Items | Where-Object IsConnected).Count
if ($renderedPortCount -ne 5) { throw "样例插入端口数量错误：$renderedPortCount" }
$presenters = @($layout.FindName('PortsItems').ItemContainerGenerator.Items | ForEach-Object {
    $layout.FindName('PortsItems').ItemContainerGenerator.ContainerFromItem($_)
})
if ($presenters.Count -ne 7 -or $presenters | Where-Object { $_ -eq $null }) { throw '未生成完整的 7 张端口卡片。' }
$firstTop = [Math]::Round($presenters[0].TranslatePoint([Windows.Point]::new(0, 0), $layout).Y, 1)
$lastTop = [Math]::Round($presenters[6].TranslatePoint([Windows.Point]::new(0, 0), $layout).Y, 1)
if ([Math]::Abs($firstTop - $lastTop) -gt 1) { throw "7 个端口未排在同一行：首卡 Y=$firstTop，末卡 Y=$lastTop" }
Write-Host "WPF 离屏渲染通过：$resolvedOutput"
Write-Host "首次启动 0 口；设置窗口新增、调序、删除通过；三态素材与失败后插拔恢复通过；演示配置的 7 个端口全部在同一行，已插入样例 $renderedPortCount 个；未调用 Show、未扫描或写入物理设备。"
$app.Shutdown()
