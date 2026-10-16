[CmdletBinding()]
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\dist\offline\UsbBatchTool.Offline.dll'),
    [switch]$TestCopy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$resolvedAssembly = [IO.Path]::GetFullPath($AssemblyPath)
if (-not [IO.File]::Exists($resolvedAssembly)) {
    throw "找不到离线程序集，请先运行 build-offline.ps1：$resolvedAssembly"
}

if (-not $TestCopy) {
    # Windows locks a loaded DLL until process exit. Run the test in a child,
    # then remove only its unique directory after the child has exited.
    $scratchParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\test-data'))
    [IO.Directory]::CreateDirectory($scratchParent) | Out-Null
    $scratchRoot = Join-Path $scratchParent ('settings-tests-' + [Guid]::NewGuid().ToString('N'))
    if ([IO.Directory]::Exists($scratchRoot) -or [IO.File]::Exists($scratchRoot)) {
        throw "测试目录已存在：$scratchRoot"
    }
    [IO.Directory]::CreateDirectory($scratchRoot) | Out-Null
    try {
        $testAssembly = Join-Path $scratchRoot ([IO.Path]::GetFileName($resolvedAssembly))
        [IO.File]::Copy($resolvedAssembly, $testAssembly)
        $pwshExe = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        & $pwshExe -NoProfile -File $PSCommandPath -AssemblyPath $testAssembly -TestCopy
        if ($LASTEXITCODE -ne 0) { throw "设置测试子进程失败，退出码 $LASTEXITCODE。" }
    }
    finally {
        $full = [IO.Path]::GetFullPath($scratchRoot)
        $parent = [IO.Path]::GetFullPath($scratchParent).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($full), $parent, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($full).StartsWith('settings-tests-', [StringComparison]::Ordinal) -or
            ([IO.File]::GetAttributes($full) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "测试目录验证失败，未执行清理：$full"
        }
        [IO.Directory]::Delete($full, $true)
    }
    return
}

# The child loads only its private copy. It never touches the user's settings.
[Reflection.Assembly]::LoadFrom($resolvedAssembly) | Out-Null
$expectedBase = [IO.Path]::GetDirectoryName($resolvedAssembly).TrimEnd([IO.Path]::DirectorySeparatorChar)
$actualBase = [IO.Path]::GetFullPath([UsbBatchTool.Services.AppPaths]::Base).TrimEnd([IO.Path]::DirectorySeparatorChar)
$allowedParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\test-data')).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not [string]::Equals($actualBase, $expectedBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([IO.Path]::GetDirectoryName($actualBase), $allowedParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($actualBase).StartsWith('settings-tests-', [StringComparison]::Ordinal)) {
    throw "测试隔离失败：AppPaths.Base=$actualBase"
}

$assertions = 0
$newSettings = [UsbBatchTool.AppSettings]::new()
if ($newSettings.Ports.Count -ne 0) { throw '新安装配置应默认 0 个端口。' }
$assertions++

$store = [UsbBatchTool.Services.SettingsStore]::new()
$loaded = $store.Load()
if ($loaded.Ports.Count -ne 0) { throw '没有 settings.json 时应加载 0 个端口。' }
$assertions++

$store.Save($loaded)
$savedZero = $store.Load()
if ($savedZero.Ports.Count -ne 0) { throw '保存 0 个端口后重新启动，不能自动补成 7 个。' }
$assertions++

$legacy = [UsbBatchTool.AppSettings]::new()
for ($i = 1; $i -le 7; $i++) {
    $binding = [UsbBatchTool.PortBinding]::new()
    $binding.Order = $i
    $binding.Name = "自定义口 $i"
    $binding.LocationPaths.Add("PCIROOT(0)#USB($i)")
    $legacy.Ports.Add($binding)
}
$store.Save($legacy)
$restored = $store.Load()
if ($restored.Ports.Count -ne 7) { throw '现有 7 口配置加载后数量改变。' }
$assertions++
for ($i = 0; $i -lt 7; $i++) {
    $port = $restored.Ports[$i]
    if ($port.Order -ne ($i + 1) -or $port.Name -ne "自定义口 $($i + 1)" -or
        $port.LocationPaths.Count -ne 1 -or $port.LocationPaths[0] -ne "PCIROOT(0)#USB($($i + 1))") {
        throw "现有配置的第 $($i + 1) 个端口未完整保留。"
    }
    $assertions++
}

# A synthetic, safety-verified USB disk exercises discovery without scanning
# hardware. Location path, rather than serial number, is the port identity.
$diskA = [UsbBatchTool.UsbDisk]::new()
$diskA.Number = 42
$diskA.Model = 'Synthetic A'
$diskA.SerialNumber = 'SYNTH-A'
$diskA.PnpDeviceId = 'USB\VID_TEST&A'
$diskA.LocationPaths.Add('PCIROOT(0)#USB(1)')
$diskA.LocationPaths.Add('ACPI(ROOT)#USB(1)')
$diskA.SizeBytes = 16000000000
$diskA.BusType = 7
$diskA.IsRemovable = $true
$diskA.SafetyVerified = $true
$diskB = [UsbBatchTool.UsbDisk]::new()
$diskB.Number = 43
$diskB.Model = 'Synthetic B'
$diskB.SerialNumber = 'SYNTH-B'
$diskB.PnpDeviceId = 'USB\VID_TEST&B'
$diskB.LocationPaths.Add('PCIROOT(0)#USB(2)')
$diskB.SizeBytes = 32000000000
$diskB.BusType = 7
$diskB.IsRemovable = $true
$diskB.SafetyVerified = $true

$tracker = [UsbBatchTool.PortDiscoveryTracker]::new()
$emptyPorts = [System.Collections.Generic.List[UsbBatchTool.PortBinding]]::new()
$visible = [System.Collections.Generic.List[UsbBatchTool.UsbDisk]]::new()
$visible.Add($diskA)
if ($tracker.NextCandidate($visible, $emptyPorts).IdentityKey -ne $diskA.IdentityKey) { throw '首次插入 A 未提示添加。' }
$assertions++
if ($null -ne $tracker.NextCandidate($visible, $emptyPorts)) { throw '同一次插入被重复提示。' }
$assertions++
$portA = [UsbBatchTool.PortDiscoveryTracker]::CreateBinding($diskA, $emptyPorts)
if ($null -eq $portA -or $portA.Order -ne 1 -or $portA.Name -ne '端口 1' -or $portA.LocationPaths.Count -ne 2) {
    throw 'A 未按位置路径创建端口 1。'
}
$assertions++
$emptyPorts.Add($portA)
$onePortSettings = [UsbBatchTool.AppSettings]::new()
$onePortSettings.Ports.Add($portA)
$store.Save($onePortSettings)
$loadedA = $store.Load()
if ($loadedA.Ports.Count -ne 1 -or -not $loadedA.Ports[0].Matches($diskA)) { throw '新绑定的端口 1 未保存并恢复。' }
$assertions++
$visible.Add($diskB)
if ($tracker.NextCandidate($visible, $emptyPorts).IdentityKey -ne $diskB.IdentityKey) { throw '插入 B 未提示添加。' }
$assertions++
$declined = $tracker.NextCandidate($visible, $emptyPorts)
if ($null -ne $declined) { throw '拒绝添加后，持续连接的设备不应重复提示。' }
$assertions++
$portB = [UsbBatchTool.PortDiscoveryTracker]::CreateBinding($diskB, $emptyPorts)
if ($null -eq $portB -or $portB.Order -ne 2 -or $portB.Name -ne '端口 2') { throw 'B 未按顺序创建端口 2。' }
$assertions++
$emptyPorts.Add($portB)
if ([UsbBatchTool.PortDiscoveryTracker]::CanOffer($diskA, $emptyPorts)) { throw '已绑定的 A 不能重复添加。' }
$assertions++

$tracker.NextCandidate([System.Collections.Generic.List[UsbBatchTool.UsbDisk]]::new(), $emptyPorts) | Out-Null
$emptyPorts.Clear()
if ($tracker.NextCandidate($visible, $emptyPorts).IdentityKey -ne $diskA.IdentityKey) { throw '拔出后再次插入未重新提示。' }
$assertions++
$unsafeDisk = [UsbBatchTool.UsbDisk]::new()
$unsafeDisk.Number = 44
$unsafeDisk.SerialNumber = 'UNSAFE'
$unsafeDisk.PnpDeviceId = 'USB\VID_TEST&UNSAFE'
$unsafeDisk.LocationPaths.Add('PCIROOT(0)#USB(3)')
$unsafeDisk.SizeBytes = 1000000000
$unsafeDisk.BusType = 7
$unsafeDisk.IsRemovable = $true
if (-not [UsbBatchTool.PortDiscoveryTracker]::CanOffer($unsafeDisk, $emptyPorts) -or
    $null -eq [UsbBatchTool.PortDiscoveryTracker]::CreateBinding($unsafeDisk, $emptyPorts) -or $unsafeDisk.CanWrite) {
    throw '未通过写入检查的 USB 应允许绑定位置，但不能写入。'
}
$assertions++
$unsafeDisk.SerialNumber = ''
if (-not $unsafeDisk.CanBind -or $unsafeDisk.CanWrite) { throw '有位置路径但无序列号的 USB 应允许绑定，继续禁止写入。' }
$assertions++
$unsafeDisk.IsSystem = $true
if ([UsbBatchTool.PortDiscoveryTracker]::CanOffer($unsafeDisk, $emptyPorts)) { throw '系统 USB 磁盘不能绑定。' }
$assertions++
$unsafeDisk.IsSystem = $false
$unsafeDisk.IsBoot = $true
if ($unsafeDisk.CanBind) { throw '启动 USB 磁盘不能绑定。' }
$assertions++
$unsafeDisk.IsBoot = $false
$unsafeDisk.BusType = 17
if ($unsafeDisk.CanBind) { throw '非 USB 磁盘不能绑定。' }
$assertions++
$unsafeDisk.BusType = 7
$unsafeDisk.LocationPaths.Clear()
if ($unsafeDisk.CanBind) { throw '没有位置路径或设备身份的磁盘不能绑定。' }
$assertions++

Write-Host "设置测试通过：$assertions 个断言；首次 0 口、保存 0 口及现有 7 口配置均正确。"
Write-Host '设备发现测试通过：A/B 顺序添加、去重、重插提示、绑定与写入分离及系统盘过滤。'
Write-Host "测试配置仅位于独立临时目录：$expectedBase"
