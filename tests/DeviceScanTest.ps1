[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AssemblyPath,
    [switch]$RequireUsbDevice
)

$ErrorActionPreference = 'Stop'
# Read-only hardware probe: it never persists bindings, opens write handles,
# formats disks or starts an application window.
[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath)) | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$devices = [UsbBatchTool.Services.DeviceScanner]::new().ScanAsync().GetAwaiter().GetResult()
$watch.Stop()
if ($RequireUsbDevice -and $devices.Count -eq 0) { throw '未检测到 USB 存储设备。' }
$ports = [Collections.Generic.List[UsbBatchTool.PortBinding]]::new()
$offered = 0
foreach ($disk in $devices) {
    if ($disk.BusType -ne 7) { throw '扫描结果包含非 USB 磁盘。' }
    if (($disk.IsSystem -or $disk.IsBoot) -and ($disk.CanBind -or $disk.CanWrite)) {
        throw '系统盘或启动盘通过了绑定/写入检查。'
    }
    if ($disk.CanBind) {
        if (-not [UsbBatchTool.PortDiscoveryTracker]::CanOffer($disk, $ports)) { throw '可绑定设备未进入添加候选。' }
        $binding = [UsbBatchTool.PortDiscoveryTracker]::CreateBinding($disk, $ports)
        if ($null -eq $binding -or -not $binding.Matches($disk)) { throw '设备不能创建匹配的位置绑定。' }
        $offered++
    }
}
$devices | Select-Object Number,Model,CapacityText,CanBind,CanWrite,SafetyReason |
    Format-List
Write-Host "只读扫描通过：USB $($devices.Count) 台，可提示添加 $offered 台，耗时 $([math]::Round($watch.Elapsed.TotalSeconds, 2)) 秒。未写入磁盘或保存配置。"
