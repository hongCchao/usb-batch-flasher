using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace UsbBatchTool.Services;

/// <summary>
/// Enumerates physical USB storage, including raw/Linux disks without a drive letter.
/// Disk numbers and drive letters are intentionally never used as persisted identity.
/// </summary>
public sealed class DeviceScanner
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfDeviceInterface = 0x00000010;
    private static readonly Guid DiskInterfaceClass = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpService = 0x00000004;
    private const uint SpdrpLocationInformation = 0x0000000d;
    private const uint SpdrpLocationPaths = 0x00000023;
    private const uint CrSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Regex UsbVidPidRegex = new(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<IReadOnlyList<UsbDisk>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static IReadOnlyList<UsbDisk> Scan(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("USB 设备扫描仅支持 Windows。");

        // Storage's IsBoot/IsSystem are needed even for USB boot disks. Failure is
        // represented by a missing entry; the corresponding target stays read-only.
        var safetyDisks = ReadStorageDisks(cancellationToken);
        var allDisks = ReadNativeDisks(cancellationToken);
        var volumes = ReadVolumes(cancellationToken);
        var protectedDiskNumbers = GetAdditionalProtectedDisks(cancellationToken, out var protectedVolumeCheckSucceeded);
        var results = new List<UsbDisk>();

        foreach (var disk in allDisks.OrderBy(d => d.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaceWasUsb = disk.BusType == 7;
            safetyDisks.TryGetValue(disk.Number, out var safety);
            if (safety is not null)
            {
                disk.IsBoot = safety.IsBoot;
                disk.IsSystem = safety.IsSystem;
                disk.IsReadOnly = safety.IsReadOnly;
                disk.BusType = safety.BusType;
                if (safety.SizeBytes > 0) disk.SizeBytes = safety.SizeBytes;
            }

            bool descriptorVerified = false;
            bool diskNumberVerified = false;
            bool geometryVerified = false;
            string nativeError = "";
            try
            {
                using var handle = NativeDisk.OpenMetadata(disk.DevicePath);
                diskNumberVerified = NativeDisk.TryGetDeviceNumber(handle, out var actualNumber) && actualNumber == disk.Number;
                var descriptor = NativeDisk.QueryStorageInfo(handle);
                if (descriptor is not null)
                {
                    descriptorVerified = true;
                    disk.BusType = descriptor.BusType;
                    disk.IsRemovable = NativeDisk.QueryHotplug(handle) == true || descriptor.Removable == true;
                    if (!string.IsNullOrWhiteSpace(descriptor.Model)) disk.Model = descriptor.Model;
                    if (!string.IsNullOrWhiteSpace(descriptor.SerialNumber)) disk.SerialNumber = descriptor.SerialNumber;
                }

                var size = NativeDisk.QueryDiskSize(handle);
                if (size > 0)
                {
                    disk.SizeBytes = size;
                    geometryVerified = true;
                }
                disk.SectorSize = NativeDisk.QuerySectorSize(handle);
                disk.IsReadOnly = NativeDisk.QueryReadOnly(handle) ?? disk.IsReadOnly;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                nativeError = ex.Message;
            }

            // Listing inaccessible USB devices is useful, but never makes them eligible
            // for selection merely because their WMI caption happens to contain USB.
            if (disk.BusType != 7 && !interfaceWasUsb && safety?.BusType != 7) continue;
            if (protectedDiskNumbers.Contains(disk.Number)) disk.IsSystem = true;
            if (volumes.TryGetValue(disk.Number, out var diskVolumes)) disk.Volumes = diskVolumes;
            try { ReadTopology(disk); }
            catch (Exception ex) when (ex is not OperationCanceledException) { nativeError += ex.Message; }

            disk.SafetyVerified = safety is not null && safety.HasSafetyFlags && descriptorVerified &&
                                  diskNumberVerified && geometryVerified && protectedVolumeCheckSucceeded && disk.BusType == 7;
            disk.SafetyReason = disk.IsBoot || disk.IsSystem
                ? "系统盘、启动盘或分页文件所在磁盘，禁止写入。"
                : !protectedVolumeCheckSucceeded
                    ? "无法核实 Windows/分页文件所在磁盘，禁止写入。"
                    : safety is null || !safety.HasSafetyFlags
                        ? "无法获取系统磁盘安全标志，禁止写入（请以管理员身份运行）。"
                        : !descriptorVerified || !diskNumberVerified || !geometryVerified
                            ? $"无法核实物理设备身份或容量，禁止写入。{nativeError}"
                            : disk.BusType != 7
                                ? "此设备未被原生存储接口确认是 USB 磁盘，禁止写入。"
                                : !disk.IsRemovable
                                    ? "此设备是 USB 固定介质或无法核实可移动属性，默认禁止批量清空。"
                                : string.IsNullOrWhiteSpace(disk.SerialNumber)
                                    ? "设备未提供可验证的序列号，批量写入可能在换插时误认，已禁止选择。"
                                : disk.IsReadOnly
                                    ? "设备为只读；开始烧录时将尝试解除软件只读，硬件写保护需手动关闭。"
                                    : "已核实 USB 物理磁盘与系统盘安全标志。";
            results.Add(disk);
        }

        // Some USB bridges expose the same non-empty serial for every unit.
        // Such a value cannot distinguish targets in a batch operation.
        foreach (var group in results.Where(d => !string.IsNullOrWhiteSpace(d.SerialNumber))
                     .GroupBy(d => d.SerialNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var duplicate in group)
            {
                duplicate.SafetyVerified = false;
                duplicate.SafetyReason = "多个已连接 USB 磁盘报告相同序列号，无法安全区分，已禁止写入。";
            }
        }

        return results;
    }

    private sealed class SafetyDisk
    {
        internal bool IsBoot;
        internal bool IsSystem;
        internal bool IsReadOnly;
        internal bool HasSafetyFlags;
        internal uint BusType;
        internal long SizeBytes;
    }

    private static Dictionary<int, SafetyDisk> ReadStorageDisks(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, SafetyDisk>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT Number,BusType,Size,IsBoot,IsSystem,IsReadOnly FROM MSFT_Disk");
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryInt(item["Number"], out var number)) continue;
                    result[number] = new SafetyDisk
                    {
                        BusType = ToUInt(item["BusType"]),
                        SizeBytes = ToLong(item["Size"]),
                        IsBoot = ToBool(item["IsBoot"]),
                        IsSystem = ToBool(item["IsSystem"]),
                        IsReadOnly = ToBool(item["IsReadOnly"]),
                        HasSafetyFlags = item["IsBoot"] is not null && item["IsSystem"] is not null
                    };
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Fail closed: a missing entry remains unverified in Scan. */ }
        return result;
    }

    private static List<UsbDisk> ReadNativeDisks(CancellationToken cancellationToken)
    {
        var result = new List<UsbDisk>();
        var classGuid = DiskInterfaceClass;
        var set = SetupDiGetClassDevsInterfaceW(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法枚举磁盘设备接口");
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var face = new SpDeviceInterfaceData { Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, index, ref face))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) throw new System.ComponentModel.Win32Exception(error, "无法枚举磁盘接口");
                    break;
                }
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref face, IntPtr.Zero, 0, out var size, ref data);
                if (size < 6 || size > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref face, detail, size, out _, ref data)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    using var handle = NativeDisk.OpenMetadata(path);
                    if (!NativeDisk.TryGetDeviceNumber(handle, out var number) || result.Any(d => d.Number == number)) continue;
                    var descriptor = NativeDisk.QueryStorageInfo(handle);
                    var disk = new UsbDisk
                    {
                        Number = number,
                        PnpDeviceId = GetInstanceId(set, ref data),
                        Model = descriptor?.Model ?? "未知设备",
                        SerialNumber = descriptor?.SerialNumber ?? "",
                        BusType = descriptor?.BusType ?? 0,
                        SizeBytes = NativeDisk.QueryDiskSize(handle),
                        LocationPaths = ReadMultiStringProperty(set, ref data, SpdrpLocationPaths)
                    };
                    if (disk.BusType == 0 && disk.PnpDeviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)) disk.BusType = 7;
                    result.Add(disk);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
                {
                    // Removal during enumeration must not discard the other disks.
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static HashSet<int> GetAdditionalProtectedDisks(CancellationToken cancellationToken, out bool succeeded)
    {
        var result = new HashSet<int>();
        succeeded = false;
        try
        {
            // The current Windows volume is checked independently of Storage WMI.
            // This also protects Windows-to-Go and unusual USB boot configurations.
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(systemRoot)) return result;
            using (var handle = NativeDisk.OpenMetadata(systemRoot))
            {
                if (!NativeDisk.TryGetDeviceNumber(handle, out var number)) return result;
                result.Add(number);
            }

            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = Path.GetPathRoot(ToText(item["Name"]));
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    using var handle = NativeDisk.OpenMetadata(root);
                    if (!NativeDisk.TryGetDeviceNumber(handle, out var number)) return result;
                    result.Add(number);
                }
            }
            succeeded = true;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Fail closed: the caller will disable every write target. */ }
        return result;
    }

    internal static Dictionary<int, List<VolumeInfo>> ReadVolumes(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<VolumeInfo>>();
        var buffer = new StringBuilder(1024);
        var findHandle = FindFirstVolumeW(buffer, buffer.Capacity);
        if (findHandle == InvalidHandleValue) return result;
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = buffer.ToString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    using var handle = NativeDisk.OpenMetadata(path);
                    if (!NativeDisk.TryGetDeviceNumber(handle, out var number)) continue;
                    var info = new VolumeInfo { VolumePath = path, MountPoints = GetMountPoints(path) };
                    var label = new StringBuilder(1024);
                    var fileSystem = new StringBuilder(1024);
                    if (GetVolumeInformationW(path, label, label.Capacity, out _, out _, out _, fileSystem, fileSystem.Capacity))
                    {
                        info.Label = label.ToString();
                        info.FileSystem = fileSystem.ToString();
                        if (GetDiskFreeSpaceExW(path, out var available, out var total, out _))
                        {
                            info.IsReady = true;
                            info.SizeBytes = ToLong(total);
                            info.FreeBytes = ToLong(available);
                        }
                    }
                    if (!result.TryGetValue(number, out var entries)) result[number] = entries = [];
                    entries.Add(info);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // An unrelated locked BitLocker volume, Storage Space or optical
                    // drive must not disable valid USB targets. The write path uses
                    // ReadTargetVolumes, which requires every target-specific volume.
                    // A RAW volume can be inaccessible while its disk is valid.
                }
                buffer.Clear();
            }
            while (FindNextVolumeW(findHandle, buffer, buffer.Capacity));
        }
        finally { FindVolumeClose(findHandle); }
        return result;
    }

    internal static List<VolumeInfo> ReadTargetVolumes(int diskNumber, CancellationToken cancellationToken)
    {
        // Storage WMI can enumerate a target's Windows volumes without first opening
        // every volume on the computer. Linux partitions have no MSFT_Volume and do
        // not need a Windows filesystem lock; physical writes remain sector aligned.
        var partitions = new List<string>();
        using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                   $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}"))
        {
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            using var collection = searcher.Get();
            foreach (ManagementObject partition in collection)
            {
                using (partition)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // MSFT_Partition is keyed by the inherited ObjectId property,
                    // not DiskNumber/PartitionNumber. Use WMI's actual object path.
                    var path = partition.Path.RelativePath;
                    if (string.IsNullOrWhiteSpace(path))
                        throw new IOException($"无法确定磁盘 {diskNumber} 的分区对象，已阻止写入。");
                    partitions.Add(path);
                }
            }
        }

        var result = new List<VolumeInfo>();
        foreach (var partitionPath in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{{partitionPath}}} WHERE AssocClass=MSFT_PartitionToVolume ResultClass=MSFT_Volume");
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            using var collection = searcher.Get();
            foreach (ManagementObject volume in collection)
            {
                using (volume)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = ToText(volume["Path"]);
                    if (string.IsNullOrWhiteSpace(path))
                        throw new IOException($"磁盘 {diskNumber} 存在无法确定路径的卷，已阻止写入。");
                    result.Add(new VolumeInfo { VolumePath = path });
                }
            }
        }
        return result;
    }

    private static List<string> GetMountPoints(string volumeName)
    {
        var buffer = new char[1024];
        if (!GetVolumePathNamesForVolumeNameW(volumeName, buffer, (uint)buffer.Length, out var required))
        {
            if (Marshal.GetLastWin32Error() != 234 || required > 65536) return [];
            buffer = new char[required];
            if (!GetVolumePathNamesForVolumeNameW(volumeName, buffer, (uint)buffer.Length, out _)) return [];
        }
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadTopology(UsbDisk disk)
    {
        if (string.IsNullOrWhiteSpace(disk.PnpDeviceId)) return;
        var infoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (infoSet == InvalidHandleValue) return;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(infoSet, index, ref data)) break;
                var instance = GetInstanceId(infoSet, ref data);
                if (!string.Equals(instance, disk.PnpDeviceId, StringComparison.OrdinalIgnoreCase)) continue;

                var paths = ReadMultiStringProperty(infoSet, ref data, SpdrpLocationPaths);
                if (paths.Count > 0) disk.LocationPaths = paths;
                disk.LocationInfo = ReadStringProperty(infoSet, ref data, SpdrpLocationInformation);
                var hardware = string.Join(";", ReadMultiStringProperty(infoSet, ref data, SpdrpHardwareId));
                ApplyVidPid(disk, hardware);

                var node = data.DevInst;
                for (var depth = 0; depth < 12 && CM_Get_Parent(out var parent, node, 0) == CrSuccess; depth++)
                {
                    node = parent;
                    var idBuffer = new StringBuilder(1024);
                    if (CM_Get_Device_IDW(node, idBuffer, idBuffer.Capacity, 0) != CrSuccess) break;
                    var parentId = idBuffer.ToString();
                    ApplyVidPid(disk, parentId);
                    var parentData = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                    var parentService = "";
                    if (SetupDiOpenDeviceInfoW(infoSet, parentId, IntPtr.Zero, 0, ref parentData))
                    {
                        parentService = ReadStringProperty(infoSet, ref parentData, SpdrpService);
                        var parentPaths = ReadMultiStringProperty(infoSet, ref parentData, SpdrpLocationPaths);
                        // Use one storage-device/USB-device location rather than all
                        // ancestor paths: Hub paths would falsely match every port.
                        if (disk.LocationPaths.Count == 0 && parentPaths.Count > 0 && parentId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                            disk.LocationPaths = parentPaths;
                        if (string.IsNullOrWhiteSpace(disk.LocationInfo))
                            disk.LocationInfo = ReadStringProperty(infoSet, ref parentData, SpdrpLocationInformation);
                    }
                    if ((parentId.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
                        parentService.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)) &&
                        string.IsNullOrWhiteSpace(disk.ParentHub))
                    {
                        if (!string.Equals(parentId, disk.PnpDeviceId, StringComparison.OrdinalIgnoreCase))
                            disk.ParentHub = parentId;
                    }
                }
                break;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(infoSet); }

        disk.LocationPaths = disk.LocationPaths
            .Select(NormalizeLocationPath)
            .Where(p => p.Contains("USB(", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeLocationPath(string path)
    {
        var value = path.Trim().ToUpperInvariant();
        // Windows appends a per-interface segment for composite devices. The USB
        // location before that segment identifies the socket and works for new media.
        foreach (var suffix in new[] { "#USBMI(", "#USBSTOR(", "#SCSI(", "#DISK(" })
        {
            var last = value.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (last >= 0) value = value[..last];
        }
        return value;
    }

    private static void ApplyVidPid(UsbDisk disk, string text)
    {
        if (!string.IsNullOrWhiteSpace(disk.Vid) && !string.IsNullOrWhiteSpace(disk.Pid)) return;
        var match = UsbVidPidRegex.Match(text);
        if (!match.Success) return;
        disk.Vid = match.Groups[1].Value.ToUpperInvariant();
        disk.Pid = match.Groups[2].Value.ToUpperInvariant();
    }

    private static string GetInstanceId(IntPtr infoSet, ref SpDevInfoData data)
    {
        var builder = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceIdW(infoSet, ref data, builder, builder.Capacity, out _)
            ? builder.ToString()
            : "";
    }

    private static List<string> ReadMultiStringProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        var bytes = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, bytes, (uint)bytes.Length, out var required))
        {
            if (Marshal.GetLastWin32Error() != NativeDisk.ErrorInsufficientBuffer || required > 65536) return [];
            bytes = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, bytes, (uint)bytes.Length, out required)) return [];
        }
        var length = Math.Min(bytes.Length, (int)required);
        return Encoding.Unicode.GetString(bytes, 0, length).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string ReadStringProperty(IntPtr set, ref SpDevInfoData data, uint property) =>
        ReadMultiStringProperty(set, ref data, property).FirstOrDefault() ?? "";

    private static string ToText(object? value) => Convert.ToString(value)?.Trim() ?? "";
    private static uint ToUInt(object? value) { try { return value is null ? 0 : Convert.ToUInt32(value); } catch { return 0; } }
    private static long ToLong(object? value) { try { return value is null ? 0 : checked((long)Convert.ToUInt64(value)); } catch { return 0; } }
    private static bool ToBool(object? value) { try { return value is not null && Convert.ToBoolean(value); } catch { return false; } }
    private static bool TryInt(object? value, out int number) { try { number = Convert.ToInt32(value); return number >= 0 && value is not null; } catch { number = -1; return false; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsInterfaceW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid classGuid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData face, IntPtr detail, uint size, out uint required, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr parent, uint flags, ref SpDevInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevInfoData data, StringBuilder instanceId, int instanceIdSize, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevInfoData data, uint property, out uint type, [Out] byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder name, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr findHandle, StringBuilder name, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr findHandle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volumeName, [Out] char[] paths, uint size, out uint required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder label, int labelLength, out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, int fileSystemLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string root, out ulong freeAvailable, out ulong totalBytes, out ulong freeBytes);
}
