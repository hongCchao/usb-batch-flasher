using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbBatchTool.Services;

public static class PhysicalPathGuard
{
    public static void AssertNotOnTarget(UsbDisk disk, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("源文件路径为空，已阻止写入。");
        var full = Path.GetFullPath(path);
        // A UNC path can be a share of the target USB disk on this very computer.
        // Without resolving the share back to its physical disk, fail closed.
        if (full.Length >= 3 && char.IsLetter(full[0]) && full[1] == ':' && GetDriveTypeW(full[..3]) == 4)
            throw new IOException("源文件位于映射的网络驱动器，无法确认其物理磁盘，已阻止写入。");
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException("无法确认网络共享源文件是否位于目标 U 盘，请先将源文件复制到电脑本地磁盘。");
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法确认网络共享源文件是否位于目标 U 盘，请先将源文件复制到电脑本地磁盘。");
        bool directory = Directory.Exists(full);
        if (!directory && !File.Exists(full)) throw new FileNotFoundException("源文件或程序位置不存在，已阻止写入。", full);
        using var handle = CreateFileW(full, 0, 7, IntPtr.Zero, 3, directory ? 0x02000000u : 0x00000080u, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实源文件或程序位置，已阻止写入。");
        var resolved = new StringBuilder(32768);
        uint len = GetFinalPathNameByHandleW(handle, resolved, resolved.Capacity, 0);
        if (len == 0 || len >= resolved.Capacity) throw new IOException("无法核实源文件的实际磁盘位置，已阻止写入。");
        string finalPath = resolved.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("源文件位于网络共享，无法确认其物理磁盘，已阻止写入。");
        var mount = new StringBuilder(32768);
        if (!GetVolumePathNameW(finalPath, mount, mount.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实源文件所在卷，已阻止写入。");
        var volume = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(mount.ToString(), volume, volume.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实源文件所在物理磁盘，已阻止写入。");
        using var volumeHandle = NativeDisk.OpenMetadata(volume.ToString());
        if (!NativeDisk.TryGetDeviceNumber(volumeHandle, out int number)) throw new IOException("无法核实源文件所属磁盘，已阻止写入。");
        if (number == disk.Number) throw new IOException("源文件、软件或缓存位于目标 U 盘上，请先移到电脑磁盘。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string rootPath);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder mount, int capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder volume, int capacity);
}
