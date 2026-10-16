using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbBatchTool.Services;

/// <summary>
/// Small, deliberately conservative wrapper around the Windows storage APIs used by
/// the scanner and the raw writer.  The wrapper does not choose a target disk; callers
/// must perform the safety checks before requesting a write handle.
/// </summary>
internal static class NativeDisk
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint ErrorInsufficientBuffer = 122;
    internal const uint ErrorNotReady = 21;
    internal const uint ErrorDeviceNotConnected = 1167;

    private const uint FileDeviceDisk = 0x00000007;
    private const uint FileDeviceFileSystem = 0x00000009;
    private const uint FileDeviceMassStorage = 0x0000002d;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;
    private const uint FileReadWriteAccess = 3;

    internal static readonly uint IoctlStorageGetDeviceNumber = CtlCode(FileDeviceMassStorage, 0x420, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlStorageQueryProperty = CtlCode(FileDeviceMassStorage, 0x500, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlStorageGetHotplugInfo = CtlCode(FileDeviceMassStorage, 0x305, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlDiskGetDriveGeometryEx = CtlCode(FileDeviceDisk, 0x0028, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlDiskIsWritable = CtlCode(FileDeviceDisk, 0x0009, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlDiskDeleteDriveLayout = CtlCode(FileDeviceDisk, 0x0040, MethodBuffered, FileReadWriteAccess);
    internal static readonly uint IoctlDiskGetDiskAttributes = CtlCode(FileDeviceDisk, 0x003c, MethodBuffered, FileAnyAccess);
    internal static readonly uint IoctlDiskSetDiskAttributes = CtlCode(FileDeviceDisk, 0x003d, MethodBuffered, FileReadWriteAccess);
    internal static readonly uint IoctlDiskUpdateProperties = CtlCode(FileDeviceDisk, 0x0050, MethodBuffered, FileAnyAccess);
    internal static readonly uint FsctlLockVolume = CtlCode(FileDeviceFileSystem, 6, MethodBuffered, FileAnyAccess);
    internal static readonly uint FsctlDismountVolume = CtlCode(FileDeviceFileSystem, 8, MethodBuffered, FileAnyAccess);

    internal const uint StorageDeviceProperty = 0;
    internal const uint PropertyStandardQuery = 0;
    internal const ulong DiskAttributeReadOnly = 0x0000000000000002UL;
    internal const ulong DiskAttributeOffline = 0x0000000000000001UL;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    internal sealed record StorageInfo(string Model, string SerialNumber, uint BusType, bool? Removable, string Vendor);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        internal uint PropertyId;
        internal uint QueryType;
        internal byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StorageDeviceNumber
    {
        internal uint DeviceType;
        internal uint DeviceNumber;
        internal uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageHotplugInfo
    {
        internal uint Size;
        internal byte MediaRemovable;
        internal byte MediaHotplug;
        internal byte DeviceHotplug;
        internal byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskAttributes
    {
        internal uint Version;
        internal uint Reserved1;
        internal ulong Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetDiskAttributes
    {
        internal uint Version;
        internal byte Persist;
        internal byte ReservedA;
        internal byte ReservedB;
        internal byte ReservedC;
        internal ulong Attributes;
        internal ulong AttributesMask;
        internal uint Reserved2A;
        internal uint Reserved2B;
        internal uint Reserved2C;
        internal uint Reserved2D;
    }

    internal static SafeFileHandle OpenPhysicalDisk(int number, bool writeAccess)
    {
        if (number < 0) throw new ArgumentOutOfRangeException(nameof(number));
        var path = $@"\\.\PhysicalDrive{number}";
        var handle = CreateFileW(path,
            writeAccess ? GenericRead | GenericWrite : GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法打开物理磁盘 {path}");
        }

        return handle;
    }

    internal static SafeFileHandle OpenVolume(string volumePath, bool writeAccess = true)
    {
        var path = NormalizeVolumePath(volumePath);
        var handle = CreateFileW(path,
            writeAccess ? GenericRead | GenericWrite : GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法打开卷 {path}");
        }

        return handle;
    }

    internal static SafeFileHandle OpenMetadata(string path)
    {
        // Access 0 is sufficient for storage mapping controls and lets read-only
        // enumeration work without taking a read/write handle to the volume.
        var handle = CreateFileW(NormalizeVolumePath(path), 0,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法读取卷设备信息 {path}");
        }
        return handle;
    }

    internal static string NormalizeVolumePath(string path)
    {
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':') return $@"\\.\{path}";
        if (path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return $@"\\.\{path[..2]}";
        if (path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) && path[5] == ':')
            return $@"\\.\{path.Substring(4, 2)}";
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path.TrimEnd('\\');
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return path.TrimEnd('\\');
        return path;
    }

    internal static bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned)
    {
        if (handle is null || handle.IsInvalid) throw new ArgumentException("句柄无效", nameof(handle));
        return DeviceIoControlNative(handle, code, input, inputSize, output, outputSize, out returned, IntPtr.Zero);
    }

    internal static bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, byte[]? output, out int returned)
    {
        if (handle is null || handle.IsInvalid) throw new ArgumentException("句柄无效", nameof(handle));
        GCHandle inputPin = default;
        GCHandle outputPin = default;
        try
        {
            if (input is { Length: > 0 }) inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
            if (output is { Length: > 0 }) outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
            return DeviceIoControlNative(handle, code,
                inputPin.IsAllocated ? inputPin.AddrOfPinnedObject() : IntPtr.Zero, input?.Length ?? 0,
                outputPin.IsAllocated ? outputPin.AddrOfPinnedObject() : IntPtr.Zero, output?.Length ?? 0,
                out returned, IntPtr.Zero);
        }
        finally
        {
            if (inputPin.IsAllocated) inputPin.Free();
            if (outputPin.IsAllocated) outputPin.Free();
        }
    }

    internal static bool TryGetDeviceNumber(SafeFileHandle handle, out int number)
    {
        number = -1;
        var size = Marshal.SizeOf<StorageDeviceNumber>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, buffer, size, out _)) return false;
            var value = Marshal.PtrToStructure<StorageDeviceNumber>(buffer);
            if (value.DeviceNumber > int.MaxValue) return false;
            number = (int)value.DeviceNumber;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static StorageInfo? QueryStorageInfo(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceProperty, QueryType = PropertyStandardQuery };
        var querySize = Marshal.SizeOf<StoragePropertyQuery>();
        var queryBuffer = Marshal.AllocHGlobal(querySize);
        try
        {
            Marshal.StructureToPtr(query, queryBuffer, false);
            var descriptorSizeBuffer = Marshal.AllocHGlobal(8);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, queryBuffer, querySize, descriptorSizeBuffer, 8, out _))
                    return null;
                var required = Marshal.ReadInt32(descriptorSizeBuffer, 4);
                if (required < 36 || required > 1024 * 1024) return null;

                var descriptor = Marshal.AllocHGlobal(required);
                try
                {
                    if (!DeviceIoControl(handle, IoctlStorageQueryProperty, queryBuffer, querySize, descriptor, required, out var returned) || returned < 36)
                        return null;
                    var data = new byte[returned];
                    Marshal.Copy(descriptor, data, 0, returned);
                    return ParseStorageDescriptor(data);
                }
                finally { Marshal.FreeHGlobal(descriptor); }
            }
            finally { Marshal.FreeHGlobal(descriptorSizeBuffer); }
        }
        finally { Marshal.FreeHGlobal(queryBuffer); }
    }

    private static StorageInfo ParseStorageDescriptor(byte[] data)
    {
        static string ReadOffsetString(byte[] bytes, int offsetIndex)
        {
            if (offsetIndex + 4 > bytes.Length) return string.Empty;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offsetIndex, 4));
            if (offset == 0 || offset >= bytes.Length) return string.Empty;
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0) end++;
            return Encoding.ASCII.GetString(bytes, checked((int)offset), checked((int)(end - offset))).Trim();
        }

        var vendor = ReadOffsetString(data, 12);
        var product = ReadOffsetString(data, 16);
        var revision = ReadOffsetString(data, 20);
        var serial = ReadOffsetString(data, 24);
        var busType = data.Length >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28, 4)) : 0;
        var model = string.Join(" ", new[] { vendor, product, revision }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        return new StorageInfo(string.IsNullOrWhiteSpace(model) ? "未知设备" : model,
            serial,
            busType,
            data[10] != 0,
            vendor);
    }

    internal static int QuerySectorSize(SafeFileHandle handle)
    {
        var buffer = new byte[1024];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, buffer, out var returned) || returned < 24) return 512;
        var value = BitConverter.ToInt32(buffer, 20);
        return value is >= 512 and <= 65536 && (value & (value - 1)) == 0 ? value : 512;
    }

    internal static long QueryDiskSize(SafeFileHandle handle)
    {
        var buffer = new byte[1024];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, buffer, out var returned) || returned < 32) return 0;
        var value = BitConverter.ToInt64(buffer, 24);
        return value > 0 ? value : 0;
    }

    internal static bool? QueryReadOnly(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<DiskAttributes>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IoctlDiskGetDiskAttributes, IntPtr.Zero, 0, buffer, size, out _))
            {
                var attrs = Marshal.PtrToStructure<DiskAttributes>(buffer);
                return (attrs.Attributes & DiskAttributeReadOnly) != 0;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return null;
    }

    internal static bool? QueryHotplug(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<StorageHotplugInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IoctlStorageGetHotplugInfo, IntPtr.Zero, 0, buffer, size, out _)) return null;
            var info = Marshal.PtrToStructure<StorageHotplugInfo>(buffer);
            return info.DeviceHotplug != 0 || info.MediaHotplug != 0 || info.MediaRemovable != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool TrySetReadOnly(SafeFileHandle handle, bool readOnly)
        => TrySetAttribute(handle, DiskAttributeReadOnly, readOnly);

    internal static bool TrySetOffline(SafeFileHandle handle, bool offline)
        => TrySetAttribute(handle, DiskAttributeOffline, offline);

    internal static bool? QueryOffline(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<DiskAttributes>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IoctlDiskGetDiskAttributes, IntPtr.Zero, 0, buffer, size, out _)) return null;
            return (Marshal.PtrToStructure<DiskAttributes>(buffer).Attributes & DiskAttributeOffline) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TrySetAttribute(SafeFileHandle handle, ulong mask, bool enabled)
    {
        var attrs = new SetDiskAttributes
        {
            Version = (uint)Marshal.SizeOf<SetDiskAttributes>(),
            Persist = 0,
            Attributes = enabled ? mask : 0,
            AttributesMask = mask
        };
        var size = Marshal.SizeOf<SetDiskAttributes>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(attrs, buffer, false);
            return DeviceIoControl(handle, IoctlDiskSetDiskAttributes, buffer, size, IntPtr.Zero, 0, out _);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool TryIsWritable(SafeFileHandle handle) =>
        DeviceIoControl(handle, IoctlDiskIsWritable, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);

    internal static bool TryDeleteDriveLayout(SafeFileHandle handle) =>
        DeviceIoControl(handle, IoctlDiskDeleteDriveLayout, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);

    internal static bool TryUpdateProperties(SafeFileHandle handle) =>
        DeviceIoControl(handle, IoctlDiskUpdateProperties, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);

    internal static bool TryLockVolume(SafeFileHandle handle) =>
        DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);

    internal static bool TryDismountVolume(SafeFileHandle handle) =>
        DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);

    internal static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlNative(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
