using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace UsbBatchTool;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    protected internal void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<PortBinding> Ports { get; set; } = [];
    public int ConcurrentJobs { get; set; } = 2;
    public int RefreshSeconds { get; set; } = 4;
    public bool VerifyAfterWrite { get; set; } = true;
    public string ZipFileSystem { get; set; } = "exFAT";
    public string ZipConflictPolicy { get; set; } = "Overwrite";
    public bool ZipFormatBeforeCopy { get; set; }
    public List<RecentFile> RecentFiles { get; set; } = [];
    public string LastMode { get; set; } = "Image";
    public string LastImagePath { get; set; } = "";
    public string LastZipPath { get; set; } = "";
}

public sealed class PortBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Name { get; set; } = "端口";
    public List<string> LocationPaths { get; set; } = [];
    public string FallbackDeviceKey { get; set; } = "";
    public string LastModel { get; set; } = "";
    public string LastSerial { get; set; } = "";
    public bool Matches(UsbDisk disk) => LocationPaths.Any(p => disk.LocationPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
        LocationPaths.Count == 0 && !string.IsNullOrWhiteSpace(FallbackDeviceKey) &&
        string.Equals(FallbackDeviceKey, disk.PersistentDeviceKey, StringComparison.OrdinalIgnoreCase);
}

public sealed class UsbDisk
{
    public int Number { get; set; }
    public string DevicePath => @"\\.\PhysicalDrive" + Number;
    public string Model { get; set; } = "未知设备";
    public string SerialNumber { get; set; } = "";
    public string PnpDeviceId { get; set; } = "";
    public string LocationInfo { get; set; } = "";
    public string ParentHub { get; set; } = "";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public List<string> LocationPaths { get; set; } = [];
    public uint BusType { get; set; }
    public long SizeBytes { get; set; }
    public int SectorSize { get; set; } = 512;
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public bool SafetyVerified { get; set; }
    public bool IsReadOnly { get; set; }
    public List<VolumeInfo> Volumes { get; set; } = [];
    public string SafetyReason { get; set; } = "";
    public bool IsRemovable { get; set; }
    public bool CanBind => BusType == 7 && !IsBoot && !IsSystem &&
        (LocationPaths.Count > 0 || !string.IsNullOrWhiteSpace(PersistentDeviceKey));
    public bool CanWrite => BusType == 7 && IsRemovable && SafetyVerified && !IsBoot && !IsSystem && SizeBytes > 0 && !string.IsNullOrWhiteSpace(SerialNumber);
    public string IdentityKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{PnpDeviceId}|{SerialNumber}|{SizeBytes}|{string.Join(";", LocationPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}")));
    public string PersistentDeviceKey => string.IsNullOrWhiteSpace(SerialNumber) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{PnpDeviceId}|{SerialNumber}|{Vid}|{Pid}")));
    public string CapacityText => DisplayBytes.Format(SizeBytes);
    public string DriveLetters => string.Join(" / ", Volumes.SelectMany(v => v.MountPoints).Where(p => p.Length <= 3));
    public string DisplayName => $"磁盘 {Number} · {Model} · {CapacityText}";
    public override string ToString() => DisplayName;
}

public sealed class VolumeInfo
{
    public string VolumePath { get; set; } = "";
    public List<string> MountPoints { get; set; } = [];
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsReady { get; set; }
    public string DriveRoot => MountPoints.FirstOrDefault(p => p.Length == 3 && p[1] == ':') ?? "";
}

public sealed class RecentFile
{
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "Image";
    public long SizeBytes { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime LastUsed { get; set; } = DateTime.Now;
    public string CachePath { get; set; } = "";
    [JsonIgnore] public string DisplayName => System.IO.Path.GetFileName(Path) + (System.IO.File.Exists(Path) ? "" : "  [文件不存在]");
}

public sealed record OperationProgress(string Stage, long CompletedBytes, long TotalBytes, double BytesPerSecond = 0, string? Detail = null)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(CompletedBytes * 100.0 / TotalBytes, 0, 100);
    public string SpeedText => BytesPerSecond <= 0 ? "" : DisplayBytes.Format((long)BytesPerSecond) + "/s";
    public string EtaText => BytesPerSecond <= 0 ? "" : TimeSpan.FromSeconds(Math.Max(0, (TotalBytes - CompletedBytes) / BytesPerSecond)).ToString(@"hh\:mm\:ss");
}

public static class DisplayBytes
{
    public static string Format(long bytes)
    {
        if (bytes < 0) return "未知";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
