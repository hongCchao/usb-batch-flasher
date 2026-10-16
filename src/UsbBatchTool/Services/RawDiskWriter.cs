using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbBatchTool.Services;

/// <summary>
/// Writes an already prepared raw image to one explicitly confirmed physical USB disk.
/// No disk is selected implicitly and the verified handle is held for the entire job.
/// </summary>
public sealed class RawDiskWriter
{
    private const int PreferredBufferSize = 4 * 1024 * 1024;
    private static readonly HashSet<string> ActiveDeviceKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveDeviceLock = new();

    public async Task WriteAsync(UsbDisk disk, string imagePath, bool verify,
        IProgress<OperationProgress> progress, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("原始镜像烧录仅支持 Windows。");
        if (!disk.CanWrite) throw new InvalidOperationException($"目标设备禁止写入：{disk.SafetyReason}");
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("请选择镜像文件。", nameof(imagePath));
        imagePath = Path.GetFullPath(imagePath);
        var extension = Path.GetExtension(imagePath);
        if (!extension.Equals(".img", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原始写入仅接受 .img 或 .iso；.img.gz 必须先解压为 .img。");
        if (!File.Exists(imagePath)) throw new FileNotFoundException("找不到镜像文件。", imagePath);

        var activeKey = disk.DevicePath;
        lock (ActiveDeviceLock)
        {
            if (!ActiveDeviceKeys.Add(activeKey)) throw new InvalidOperationException("同一设备已有烧录任务，请等待该任务结束。");
        }

        try
        {
            progress.Report(new OperationProgress("核实设备", 0, 1, Detail: "重新确认物理磁盘身份和系统盘保护"));
            var freshDisks = await new DeviceScanner().ScanAsync(ct).ConfigureAwait(false);
            var current = freshDisks.FirstOrDefault(d => d.Number == disk.Number);
            if (current is null) throw new IOException("目标设备已经拔出，请重新选择设备。");
            if (!current.CanWrite) throw new InvalidOperationException($"目标设备的安全检查失败：{current.SafetyReason}");
            if (!string.Equals(current.IdentityKey, disk.IdentityKey, StringComparison.Ordinal))
                throw new IOException("目标磁盘身份已发生变化，已阻止写入。请重新选择并确认目标设备。");

            if (Environment.ProcessPath is string programPath) PhysicalPathGuard.AssertNotOnTarget(current, programPath);
            PhysicalPathGuard.AssertNotOnTarget(current, AppPaths.Base);
            PhysicalPathGuard.AssertNotOnTarget(current, imagePath);

            // Synchronous Win32 handles are used deliberately: the whole operation is
            // moved off the UI thread instead of pretending the handle is overlapped.
            await Task.Run(() => WriteCore(current, imagePath, verify, progress, log, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (ActiveDeviceLock) ActiveDeviceKeys.Remove(activeKey);
        }
    }

    private static void WriteCore(UsbDisk disk, string imagePath, bool verify,
        IProgress<OperationProgress> progress, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            PreferredBufferSize, FileOptions.SequentialScan);
        if (source.Length == 0) throw new InvalidOperationException("镜像文件为空，无法烧录。");
        var finalSourcePath = GetFinalFilePath(source.SafeFileHandle);
        if (GetFileDiskNumber(finalSourcePath) == disk.Number)
            throw new InvalidOperationException("镜像文件位于目标 U 盘上，不能烧录到自身。请先将镜像复制到电脑磁盘。");

        // A software read-only flag can deny GENERIC_WRITE at CreateFile time.
        // Remove it only after safety/identity checks and before opening the write handle.
        if (disk.IsReadOnly)
        {
            using var metadata = NativeDisk.OpenPhysicalDisk(disk.Number, false);
            ValidateOpenHandle(metadata, disk);
            if (NativeDisk.QueryReadOnly(metadata) == true)
            {
                log($"[{disk.DisplayName}] 尝试解除软件只读属性。");
                if (!NativeDisk.TrySetReadOnly(metadata, false)) throw NativeDisk.LastError("无法解除软件只读属性，请检查硬件写保护。");
            }
        }

        using var diskHandle = NativeDisk.OpenPhysicalDisk(disk.Number, true);
        ValidateOpenHandle(diskHandle, disk);
        // The handle is now held open. Compare the full PNP/topology identity
        // against a new scan before clearing partitions on that exact handle.
        var heldDisk = new DeviceScanner().ScanAsync(ct).GetAwaiter().GetResult()
            .SingleOrDefault(candidate => candidate.Number == disk.Number);
        if (heldDisk is null || !heldDisk.CanWrite || heldDisk.IdentityKey != disk.IdentityKey)
            throw new IOException("打开磁盘后设备身份或端口位置发生变化，已阻止写入。");
        var sectorSize = NativeDisk.QuerySectorSize(diskHandle);
        var diskSize = NativeDisk.QueryDiskSize(diskHandle);
        var alignedImageLength = AlignUp(source.Length, sectorSize);
        if (alignedImageLength > diskSize)
            throw new InvalidOperationException($"目标容量不足：镜像 {DisplayBytes.Format(source.Length)}，目标 {DisplayBytes.Format(diskSize)}。");

        log($"[{disk.DisplayName}] 已复核 {disk.DevicePath}，序列号 {ShowSerial(disk.SerialNumber)}；镜像 {Path.GetFileName(imagePath)}，{DisplayBytes.Format(source.Length)}。");
        var lockedVolumes = new List<SafeFileHandle>();
        var destructiveOperationStarted = false;
        var succeeded = false;
        var restoreOnline = false;
        try
        {
            progress.Report(new OperationProgress("卸载卷", 0, source.Length, Detail: "锁定并卸载目标设备的所有现有卷"));
            LockExistingVolumes(disk, lockedVolumes, log, ct);
            ct.ThrowIfCancellationRequested();

            // Do not modify unrelated attributes. A hardware write-protect switch
            // cannot be disabled by this API and remains a clear error below.
            if (NativeDisk.QueryReadOnly(diskHandle) == true)
            {
                log($"[{disk.DisplayName}] 尝试解除该设备的软件只读属性。");
                if (!NativeDisk.TrySetReadOnly(diskHandle, false))
                    throw NativeDisk.LastError("无法解除目标磁盘的只读属性，请检查硬件写保护。");
            }
            if (!NativeDisk.TryIsWritable(diskHandle))
                throw NativeDisk.LastError("目标磁盘不可写，请检查写保护或设备状态。");

            // Windows can repair/expand a newly written GPT while the USB disk is
            // online, changing sector 1 before read-back verification finishes.
            // Temporary, non-persistent offline state suppresses filesystem and
            // partition-manager interference while retaining this exact handle.
            var wasOffline = NativeDisk.QueryOffline(diskHandle)
                ?? throw new IOException("无法确认目标磁盘脱机状态，尚未开始写入。");
            if (!wasOffline)
            {
                restoreOnline = true;
                if (!NativeDisk.TrySetOffline(diskHandle, true) || NativeDisk.QueryOffline(diskHandle) != true)
                    throw NativeDisk.LastError("无法临时脱机目标磁盘，已阻止写入以避免分区表被系统改动。");
            }
            log($"[{disk.DisplayName}] 已临时脱机；写入和校验期间禁止系统自动挂载或调整 GPT。");

            // Keep the verified handle. Raw disk handles are not reliably seekable
            // through FileStream; use explicit Win32 offsets for every operation.
            using var target = new PhysicalDiskStream(diskHandle, diskSize, sectorSize, writable: true);
            ct.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress("清理旧分区", 0, source.Length, Detail: "清除旧分区头和磁盘尾部的旧 GPT 备份"));
            destructiveOperationStarted = true;

            // A bridge may reject DELETE_DRIVE_LAYOUT. Explicitly clearing the
            // beginning and end also handles damaged layouts and stale backup GPT.
            if (!NativeDisk.TryDeleteDriveLayout(diskHandle))
                log($"[{disk.DisplayName}] 驱动不支持删除分区布局（Win32 {Marshal.GetLastWin32Error()}），改为清理磁盘首尾分区信息。");
            ClearOldLayout(target, diskSize, sectorSize, ct);
            target.Flush();
            log($"[{disk.DisplayName}] 已清理旧分区信息，开始从物理磁盘偏移 0 写入。");

            CopyImage(source, target, sectorSize, progress, ct);
            target.Flush();
            log($"[{disk.DisplayName}] 镜像写入完成，已刷新设备缓存。");

            if (verify)
            {
                VerifyImage(source, target, sectorSize, progress, ct);
                log($"[{disk.DisplayName}] 逐字节读回校验通过。");
            }

            ct.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress("刷新分区", source.Length, source.Length));
            // The original verified handle is still held open at this point.
            if (!NativeDisk.TryUpdateProperties(diskHandle))
                log($"[{disk.DisplayName}] 数据已完成，但刷新分区失败（Win32 {Marshal.GetLastWin32Error()}）；可能需要重新插拔 U 盘。");
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            log($"[{disk.DisplayName}] 已取消。{(destructiveOperationStarted ? "目标数据可能不完整，需重新烧录。" : "尚未开始清理或写入镜像。")}");
            throw;
        }
        catch (Exception ex)
        {
            var suffix = destructiveOperationStarted ? "目标数据可能不完整，请重新烧录。" : "尚未开始镜像写入。";
            log($"[{disk.DisplayName}] 烧录失败：{ex.Message} {suffix}");
            throw new IOException($"{disk.DisplayName} 烧录失败：{ex.Message} {suffix}", ex);
        }
        finally
        {
            // Closing the handles releases all locks, including cancellation paths.
            foreach (var volume in lockedVolumes) volume.Dispose();
            if (restoreOnline)
            {
                if (!NativeDisk.TrySetOffline(diskHandle, false) || NativeDisk.QueryOffline(diskHandle) != false)
                    log($"[{disk.DisplayName}] 警告：恢复联机失败，请重新插拔 U 盘；Win32 {Marshal.GetLastWin32Error()}。");
                else
                {
                    NativeDisk.TryUpdateProperties(diskHandle);
                    log($"[{disk.DisplayName}] 已恢复联机状态。");
                }
            }
        }

        if (succeeded)
        {
            progress.Report(new OperationProgress("已完成", source.Length, source.Length, Detail: verify ? "写入和校验成功" : "写入成功"));
            log($"[{disk.DisplayName}] 任务完成。");
        }
    }

    private static void ValidateOpenHandle(SafeFileHandle handle, UsbDisk disk)
    {
        if (!NativeDisk.TryGetDeviceNumber(handle, out var number) || number != disk.Number)
            throw new IOException("打开的物理磁盘编号与确认目标不一致，已阻止写入。");
        var descriptor = NativeDisk.QueryStorageInfo(handle)
            ?? throw new IOException("无法读取打开设备的硬件身份，已阻止写入。");
        if (descriptor.BusType != 7) throw new IOException("打开的设备不是已确认的 USB 磁盘，已阻止写入。");
        if (!string.IsNullOrWhiteSpace(disk.SerialNumber) &&
            !string.Equals(descriptor.SerialNumber.Trim(), disk.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("打开设备的序列号已变化，已阻止写入。");
        var size = NativeDisk.QueryDiskSize(handle);
        if (size <= 0 || size != disk.SizeBytes) throw new IOException("打开设备的容量已变化，已阻止写入。");
        if (string.IsNullOrWhiteSpace(disk.SerialNumber) &&
            !string.Equals(descriptor.Model, disk.Model, StringComparison.OrdinalIgnoreCase))
            throw new IOException("设备无序列号且型号发生变化，已阻止写入。");
    }

    private static void LockExistingVolumes(UsbDisk disk, List<SafeFileHandle> locks,
        Action<string> log, CancellationToken ct)
    {
        // Re-enumerate GUID volume names immediately before locking. Drive letters
        // alone miss Linux/RAW partitions and are unsafe to persist between scans.
        var currentVolumes = DeviceScanner.ReadTargetVolumes(disk.Number, ct);
        var volumePaths = disk.Volumes.Select(v => v.VolumePath)
            .Concat(currentVolumes.Select(v => v.VolumePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in volumePaths)
        {
            ct.ThrowIfCancellationRequested();
            SafeFileHandle? handle = null;
            try
            {
                handle = NativeDisk.OpenVolume(path);
                if (!NativeDisk.TryGetDeviceNumber(handle, out var number) || number != disk.Number)
                    throw new IOException($"卷 {path} 的磁盘身份已变化，已阻止写入。");
                if (!NativeDisk.TryLockVolume(handle))
                    throw NativeDisk.LastError($"无法独占锁定卷 {path}。请关闭资源管理器和正在使用该 U 盘的程序。");
                if (!NativeDisk.TryDismountVolume(handle))
                    throw NativeDisk.LastError($"无法卸载卷 {path}，已阻止写入。");
                locks.Add(handle);
                handle = null;
                log($"[{disk.DisplayName}] 已锁定并卸载卷 {path}。");
            }
            finally { handle?.Dispose(); }
        }
    }

    private static void ClearOldLayout(Stream target, long diskSize, int sectorSize, CancellationToken ct)
    {
        var cleanSize = Math.Min(diskSize, AlignUp(1024 * 1024, sectorSize));
        cleanSize -= cleanSize % sectorSize;
        var zeros = new byte[checked((int)cleanSize)];
        ct.ThrowIfCancellationRequested();
        target.Position = 0;
        target.Write(zeros);
        var tailPosition = diskSize - cleanSize;
        tailPosition -= tailPosition % sectorSize;
        if (tailPosition > 0)
        {
            ct.ThrowIfCancellationRequested();
            target.Position = tailPosition;
            target.Write(zeros);
        }
        target.Position = 0;
    }

    private static void CopyImage(Stream source, Stream target, int sectorSize,
        IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var bufferSize = checked((int)AlignUp(PreferredBufferSize, sectorSize));
        var buffer = new byte[bufferSize];
        source.Position = 0;
        target.Position = 0;
        long completed = 0;
        var elapsed = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        progress.Report(new OperationProgress("写入中", 0, source.Length));
        while (completed < source.Length)
        {
            ct.ThrowIfCancellationRequested();
            var wanted = checked((int)Math.Min(buffer.Length, source.Length - completed));
            ReadExactly(source, buffer, wanted);
            var writeLength = checked((int)AlignUp(wanted, sectorSize));
            if (writeLength > wanted) Array.Clear(buffer, wanted, writeLength - wanted);
            target.Write(buffer, 0, writeLength);
            completed += wanted;
            if (elapsed.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100) || completed == source.Length)
            {
                lastReport = elapsed.Elapsed;
                progress.Report(new OperationProgress("写入中", completed, source.Length,
                    completed / Math.Max(elapsed.Elapsed.TotalSeconds, 0.001)));
            }
        }
    }

    private static void VerifyImage(Stream source, Stream target, int sectorSize,
        IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var bufferSize = checked((int)AlignUp(PreferredBufferSize, sectorSize));
        var sourceBytes = new byte[bufferSize];
        var actualBytes = new byte[bufferSize];
        source.Position = 0;
        target.Position = 0;
        long completed = 0;
        var elapsed = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        progress.Report(new OperationProgress("校验中", 0, source.Length, Detail: "逐字节读回校验"));
        while (completed < source.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(bufferSize, source.Length - completed));
            ReadExactly(source, sourceBytes, count);
            // A physical disk read may require a whole sector for the final block.
            var readCount = checked((int)AlignUp(count, sectorSize));
            ReadExactly(target, actualBytes, readCount);
            if (!sourceBytes.AsSpan(0, count).SequenceEqual(actualBytes.AsSpan(0, count)))
            {
                var mismatch = 0;
                while (mismatch < count && sourceBytes[mismatch] == actualBytes[mismatch]) mismatch++;
                throw new IOException($"读回校验不一致，准确偏移 {completed + mismatch:N0} 字节；" +
                    $"预期 0x{sourceBytes[mismatch]:X2}，实际 0x{actualBytes[mismatch]:X2}。");
            }
            completed += count;
            if (elapsed.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100) || completed == source.Length)
            {
                lastReport = elapsed.Elapsed;
                progress.Report(new OperationProgress("校验中", completed, source.Length,
                    completed / Math.Max(elapsed.Elapsed.TotalSeconds, 0.001)));
            }
        }
    }

    private static void ReadExactly(Stream source, byte[] buffer, int count)
    {
        var completed = 0;
        while (completed < count)
        {
            var read = source.Read(buffer, completed, count - completed);
            if (read == 0) throw new EndOfStreamException("读取镜像或目标设备时提前到达文件末尾，设备可能已拔出。");
            completed += read;
        }
    }

    private static long AlignUp(long count, int alignment) =>
        checked(((count + alignment - 1) / alignment) * alignment);

    private static string ShowSerial(string serial) => string.IsNullOrWhiteSpace(serial) ? "（无序列号）" : serial;

    private static string GetFinalFilePath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, builder, builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确定镜像文件的实际路径，已阻止写入。");
        return builder.ToString();
    }

    private static int? GetFileDiskNumber(string filePath)
    {
        // A local share can also expose the target disk. Never assume UNC is remote.
        if (filePath.StartsWith(@"\\", StringComparison.Ordinal) && !filePath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            filePath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法确认网络共享镜像所在的物理磁盘，已阻止写入。");
        var mount = new StringBuilder(32768);
        if (!GetVolumePathNameW(filePath, mount, mount.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实镜像文件所在卷，已阻止写入。");
        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(mount.ToString(), volumeName, volumeName.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实镜像文件所在磁盘，已阻止写入。");
        using var handle = NativeDisk.OpenMetadata(volumeName.ToString());
        if (!NativeDisk.TryGetDeviceNumber(handle, out var number))
            throw new IOException("无法核实镜像文件所在磁盘，已阻止写入。");
        return number;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder filePath, int bufferLength, uint flags);
}
