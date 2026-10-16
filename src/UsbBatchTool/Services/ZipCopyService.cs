using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbBatchTool.Services;

public sealed class ZipCopyService
{
    private readonly DeviceScanner _scanner = new();

    public async Task CopyAsync(UsbDisk expected, string sourceDirectory, bool format, string fileSystem, string conflictPolicy,
        Func<string, Task<bool>> confirmOverwrite, IProgress<OperationProgress> progress, Action<string> log, CancellationToken ct)
    {
        var current = await RevalidateAsync(expected, ct);
        if (!current.IsRemovable || string.IsNullOrWhiteSpace(current.SerialNumber)) throw new IOException("设备缺少可移动介质或稳定序列号信息，已阻止复制。");
        PhysicalPathGuard.AssertNotOnTarget(current, sourceDirectory);
        PhysicalPathGuard.AssertNotOnTarget(current, AppPaths.Base);
        if (Environment.ProcessPath is string processPath) PhysicalPathGuard.AssertNotOnTarget(current, processPath);
        // Source is opened/prepared before any destructive step; enumerate once for deterministic totals.
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        var directories = Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories);
        long total = files.Sum(f => new FileInfo(f).Length);
        if (files.Length == 0) throw new IOException("ZIP 中没有可复制的文件。");
        if (fileSystem == "FAT32" && files.Any(f => new FileInfo(f).Length >= 4L * 1024 * 1024 * 1024))
            throw new IOException("FAT32 不支持 4 GiB 或更大的单个文件，请选择 exFAT 或 NTFS。");
        if (format)
        {
            if (total + 32L * 1024 * 1024 > current.SizeBytes) throw new IOException("目标 U 盘容量不足，未执行格式化。");
            progress.Report(new("重建文件盘", 0, total, 0, "清除旧分区并快速格式化"));
            await FormatAsync(current, fileSystem, log, ct);
            for (int retry = 0; retry < 20; retry++)
            {
                ct.ThrowIfCancellationRequested();
                current = await RevalidateAsync(expected, ct);
                if (current.Volumes.Any(v => v.IsReady && v.DriveRoot.Length > 0)) break;
                await Task.Delay(500, ct);
            }
        }
        var volumes = current.Volumes.Where(v => v.IsReady && v.DriveRoot.Length > 0).ToList();
        if (volumes.Count != 1) throw new IOException(volumes.Count == 0
            ? "目标设备没有可用盘符。若之前烧录过系统，请启用“复制前重建并格式化”。"
            : "目标设备有多个可用卷，请启用“复制前重建并格式化”，避免复制到错误分区。");
        var volume = volumes[0];
        string targetRoot = volume.VolumePath.EndsWith('\\') ? volume.VolumePath : volume.VolumePath + "\\";
        if (!targetRoot.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) throw new IOException("无法取得稳定的目标卷标识，已停止复制。");
        if (volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && files.Any(f => new FileInfo(f).Length >= 4L * 1024 * 1024 * 1024)) throw new IOException("目标 FAT32 不支持 4 GiB 大文件。");
        long requiredBytes = 0;
        foreach (string sourceFile in files)
        {
            string destination = CacheService.SafeChildPath(targetRoot, Path.GetRelativePath(sourceDirectory, sourceFile));
            RejectReparsePoints(targetRoot, destination);
            long size = new FileInfo(sourceFile).Length;
            if (File.Exists(destination))
            {
                if (conflictPolicy == "Skip") continue;
                // An overwrite is first staged beside the old file, then atomically renamed.
                // Until that rename finishes both copies need room.
            }
            requiredBytes = checked(requiredBytes + size);
        }
        long reserve = Math.Min(16L * 1024 * 1024, volume.SizeBytes / 100);
        if (volume.FreeBytes < requiredBytes + reserve) throw new IOException($"目标剩余空间不足：需要约 {DisplayBytes.Format(requiredBytes)}，剩余 {DisplayBytes.Format(volume.FreeBytes)}。");

        // Resolve drive-letter reuse on each file. Never resume onto a replacement device.
        foreach (string dir in directories)
        {
            ct.ThrowIfCancellationRequested();
            VerifyDriveMapping(expected, volume);
            string destination = CacheService.SafeChildPath(targetRoot, Path.GetRelativePath(sourceDirectory, dir));
            RejectReparsePoints(targetRoot, destination);
            Directory.CreateDirectory(destination);
        }
        byte[] buffer = new byte[1024 * 1024];
        long done = 0;
        var watch = Stopwatch.StartNew();
        foreach (string source in files)
        {
            ct.ThrowIfCancellationRequested();
            VerifyDriveMapping(expected, volume);
            string relative = Path.GetRelativePath(sourceDirectory, source);
            string destination = CacheService.SafeChildPath(targetRoot, relative);
            RejectReparsePoints(targetRoot, destination);
            long fileLength = new FileInfo(source).Length;
            if (File.Exists(destination))
            {
                bool overwrite = conflictPolicy == "Overwrite" || conflictPolicy == "Ask" && await confirmOverwrite(relative);
                if (!overwrite) { done += fileLength; log("跳过同名文件：" + relative); progress.Report(new("复制文件", done, total, 0, relative)); continue; }
            }
            VerifyDriveMapping(expected, volume);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string partial = destination + ".usbcopy-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    int n;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        VerifyDriveMapping(expected, volume);
                        await output.WriteAsync(buffer.AsMemory(0, n), ct); done += n;
                        progress.Report(new("复制文件", done, total, done / Math.Max(watch.Elapsed.TotalSeconds, 0.01), relative));
                    }
                    await output.FlushAsync(ct);
                    output.Flush(true);
                }
                VerifyDriveMapping(expected, volume);
                File.Move(partial, destination, true);
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
            catch
            {
                try { VerifyDriveMapping(expected, volume); File.Delete(partial); } catch { }
                throw;
            }
        }
        progress.Report(new("已完成", Math.Max(total, 1), Math.Max(total, 1), 0, $"已处理 {files.Length} 个文件"));
    }

    private async Task<UsbDisk> RevalidateAsync(UsbDisk expected, CancellationToken ct)
    {
        var devices = await _scanner.ScanAsync(ct);
        var current = devices.SingleOrDefault(d => d.Number == expected.Number);
        if (current is null || current.IdentityKey != expected.IdentityKey || !current.CanWrite) throw new IOException("目标设备已拔出、身份变化或无法确认安全，操作已停止。");
        return current;
    }

    private static void VerifyDriveMapping(UsbDisk expected, VolumeInfo volume)
    {
        using var handle = NativeDisk.OpenMetadata(volume.VolumePath);
        if (!NativeDisk.TryGetDeviceNumber(handle, out int number) || number != expected.Number) throw new IOException("目标卷映射已变化，已停止文件复制。");
        using var diskHandle = NativeDisk.OpenPhysicalDisk(number, false);
        var descriptor = NativeDisk.QueryStorageInfo(diskHandle);
        if (descriptor is null || descriptor.BusType != 7 || NativeDisk.QueryDiskSize(diskHandle) != expected.SizeBytes ||
            expected.SerialNumber.Length > 0 && !descriptor.SerialNumber.Trim().Equals(expected.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标设备身份变化，已停止文件复制。");
        using var searcher = new ManagementObjectSearcher($"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={number}");
        using var results = searcher.Get();
        bool found = false;
        foreach (ManagementObject disk in results)
        {
            using (disk) { if (!string.Equals(Convert.ToString(disk["PNPDeviceID"]), expected.PnpDeviceId, StringComparison.OrdinalIgnoreCase)) throw new IOException("目标 PNP 身份变化，已停止文件复制。"); found = true; }
        }
        if (!found) throw new IOException("目标设备已拔出。");
    }

    private static void RejectReparsePoints(string root, string destination)
    {
        string? path = destination;
        while (path is not null && path.Length >= root.Length)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("目标路径包含链接或挂载点，已停止：" + path);
            if (string.Equals(Path.GetFullPath(path).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
            path = Path.GetDirectoryName(path);
        }
    }

    private static async Task FormatAsync(UsbDisk expected, string fileSystem, Action<string> log, CancellationToken ct)
    {
        if (!new[] { "exFAT", "NTFS", "FAT32" }.Contains(fileSystem)) throw new ArgumentException("文件系统不受支持。");
        if (expected.Number < 0 || !expected.CanWrite) throw new IOException("目标设备安全检查未通过。");
        if (fileSystem == "FAT32" && expected.SizeBytes > 32L * 1024 * 1024 * 1024) throw new IOException("Windows 格式化工具不支持大于 32 GiB 的 FAT32 文件盘，请选择 exFAT。");
        // All strings below are fixed, or serialized literals. No user text is interpreted as code.
        string serialLiteral = "'" + expected.SerialNumber.Trim().Replace("'", "''") + "'";
        string pnpLiteral = "'" + expected.PnpDeviceId.Replace("'", "''") + "'";
        string script = $@"
$ErrorActionPreference = 'Stop'
$initial = Get-Disk -Number {expected.Number} -ErrorAction Stop
$originalPath = $initial.Path
$originalSerial = ([string]$initial.SerialNumber).Trim()
if ([string]::IsNullOrWhiteSpace($originalPath)) {{ throw '无法获取稳定设备路径，未格式化' }}
if ([string]::IsNullOrWhiteSpace($originalSerial)) {{ throw '此设备没有可验证序列号，未格式化' }}
function Get-ConfirmedDisk {{
    $x = Get-Disk -Number {expected.Number} -ErrorAction Stop
    if ($x.BusType -ne 'USB' -or $null -eq $x.IsBoot -or $null -eq $x.IsSystem -or $x.IsBoot -or $x.IsSystem -or $x.Size -ne {expected.SizeBytes}) {{ throw '设备安全校验失败，操作停止' }}
    if ($x.Path -ne $originalPath) {{ throw '设备路径变化，操作停止' }}
    if (([string]$x.SerialNumber).Trim() -ne $originalSerial) {{ throw '设备序列号变化，操作停止' }}
    if ({serialLiteral}.Length -gt 0 -and $originalSerial -ne {serialLiteral}) {{ throw '设备初始序列号不一致，操作停止' }}
    $w = Get-CimInstance Win32_DiskDrive -Filter 'Index={expected.Number}' -ErrorAction Stop
    if ($w.PNPDeviceID -ne {pnpLiteral}) {{ throw '设备PNP身份变化，操作停止' }}
    return $x
}}
$d = Get-ConfirmedDisk
if ($d.IsOffline) {{ throw '设备已离线，请重新插入设备' }}
if ($d.IsReadOnly) {{ Set-Disk -InputObject $d -IsReadOnly $false -ErrorAction Stop }}
$d = Get-ConfirmedDisk
if ($d.PartitionStyle -ne 'RAW') {{ Clear-Disk -InputObject $d -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop }}
$d = Get-ConfirmedDisk
$d = Initialize-Disk -InputObject $d -PartitionStyle MBR -PassThru -ErrorAction Stop
$d = Get-ConfirmedDisk
$p = New-Partition -DiskNumber $d.Number -UseMaximumSize -AssignDriveLetter -ErrorAction Stop
$d = Get-ConfirmedDisk
Format-Volume -Partition $p -FileSystem {fileSystem} -NewFileSystemLabel USB_DATA -Force -Confirm:$false -ErrorAction Stop | Out-Null
Write-Output '格式化完成'
";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows 格式化服务。");
        // Do not kill Storage operations midway. Cancellation takes effect once formatting returns.
        // A hung storage provider must not leave the task waiting forever.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var completed = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromMinutes(15)));
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync();
            throw new TimeoutException("Windows 格式化服务 15 分钟未返回，已停止任务。目标数据可能不完整，请检查后重新格式化。");
        }
        string outText = await output, errText = await error;
        if (process.ExitCode != 0) throw new IOException("格式化失败：" + errText.Trim());
        log(outText.Trim());
        ct.ThrowIfCancellationRequested();
    }

}
