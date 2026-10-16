namespace UsbBatchTool;

/// <summary>
/// Tracks devices already offered during the current insertion. A declined device
/// can be offered again after it disappears from one successful scan.
/// </summary>
public sealed class PortDiscoveryTracker
{
    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);

    public UsbDisk? NextCandidate(IReadOnlyList<UsbDisk> disks, IReadOnlyList<PortBinding> ports)
    {
        var present = disks.Select(d => d.IdentityKey).ToHashSet(StringComparer.Ordinal);
        _offered.RemoveWhere(key => !present.Contains(key));
        foreach (var disk in disks)
        {
            if (CanOffer(disk, ports) && _offered.Add(disk.IdentityKey)) return disk;
        }
        return null;
    }

    public static bool CanOffer(UsbDisk disk, IReadOnlyList<PortBinding> ports) =>
        ports.Count < 32 && disk.CanBind &&
        (disk.LocationPaths.Count > 0 || !string.IsNullOrWhiteSpace(disk.PersistentDeviceKey)) &&
        !ports.Any(port => port.Matches(disk)) &&
        !ports.Any(port => port.LocationPaths.Any(path => disk.LocationPaths.Contains(path, StringComparer.OrdinalIgnoreCase)));

    public static PortBinding? CreateBinding(UsbDisk disk, IReadOnlyList<PortBinding> ports)
    {
        if (!CanOffer(disk, ports)) return null;
        var order = ports.Count + 1;
        return new PortBinding
        {
            Order = order,
            Name = $"端口 {order}",
            LocationPaths = disk.LocationPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FallbackDeviceKey = disk.LocationPaths.Count == 0 ? disk.PersistentDeviceKey : "",
            LastModel = disk.Model,
            LastSerial = disk.SerialNumber
        };
    }
}
