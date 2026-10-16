using System.Windows.Media.Imaging;

namespace UsbBatchTool;

public sealed class PortViewModel : ObservableObject
{
    private UsbDisk? _disk;
    private bool _selected;
    private bool _busy;
    private double _progress;
    private string _stage = "等待操作";
    private string _detail = "";
    private bool _error;
    private bool _writeFailed;
    private bool _removalNotificationPending;
    public PortBinding Binding { get; }
    public PortViewModel(PortBinding binding) => Binding = binding;
    public Guid Id => Binding.Id;
    public string Name => Binding.Name;
    public string PortLabel => $"PORT {Binding.Order:00}";
    public UsbDisk? Disk
    {
        get => _disk;
        set
        {
            var previous = _disk;
            var changed = !EqualityComparer<UsbDisk?>.Default.Equals(previous, value);
            if (!changed && (! _removalNotificationPending || IsBusy)) return;
            if (changed) Set(ref _disk, value);
            if (!IsBusy && (previous is null || value is null || previous.IdentityKey != value.IdentityKey || _removalNotificationPending))
            {
                HasError = false;
                HasWriteFailure = false;
                Progress = 0;
                Stage = value is null ? "等待插入" : "等待操作";
                ProgressDetail = "";
            }
            if (!IsBusy) _removalNotificationPending = false;
            foreach (var property in new[] { nameof(IsConnected), nameof(CanSelect), nameof(StateText), nameof(DeviceText), nameof(CapacityText), nameof(VolumeText), nameof(FreeText), nameof(Details), nameof(StateImage), nameof(HasBinding) }) OnPropertyChanged(property);
            if (!CanSelect && !IsBusy) IsSelected = false;
        }
    }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) OnPropertyChanged(nameof(CanSelect)); } }
    public bool IsConnected => Disk is not null;
    public bool HasBinding => Binding.LocationPaths.Count > 0 || Binding.FallbackDeviceKey.Length > 0;
    public bool CanSelect => Disk?.CanWrite == true && !IsBusy;
    public string StateText => Disk is null ? HasBinding ? "未插入" : "未绑定端口" : HasWriteFailure ? "烧录失败" : Disk.CanWrite ? "已插入" : "受保护 / 无法确认";
    public string DeviceText => Disk?.Model ?? (HasBinding ? "等待插入 U 盘" : "请先识别或绑定此端口");
    public string CapacityText => Disk is null ? "—" : Disk.CapacityText;
    public string VolumeText => Disk is null ? "盘符 —" : string.IsNullOrEmpty(Disk.DriveLetters) ? "无盘符 · 支持镜像烧录" : Disk.DriveLetters + "  " + string.Join(" / ", Disk.Volumes.Select(v => v.Label).Where(l => l.Length > 0));
    public string FreeText => Disk is null ? "" : Disk.Volumes.Any(v => v.IsReady) ? "剩余 " + DisplayBytes.Format(Disk.Volumes.Where(v => v.IsReady).Sum(v => v.FreeBytes)) : "分区/剩余空间未知";
    public string Details => Disk is null ? Binding.FallbackDeviceKey.Length > 0 ? "设备身份回退绑定；更换 U 盘后需要重新手动绑定。" : string.Join("\n", Binding.LocationPaths) : $"{Disk.DevicePath}\n型号：{Disk.Model}\n序列号：{Disk.SerialNumber}\nVID/PID：{Disk.Vid}/{Disk.Pid}\nPNP：{Disk.PnpDeviceId}\n拓扑：{string.Join("\n", Disk.LocationPaths)}\n{Disk.SafetyReason}";
    private static BitmapImage? _connected, _disconnected, _writeFailedImage;
    public BitmapImage StateImage => !IsConnected
        ? _disconnected ??= EmbeddedAssets.Image("disconnected.png")
        : HasWriteFailure ? _writeFailedImage ??= EmbeddedAssets.Image("failed.png")
        : _connected ??= EmbeddedAssets.Image("connected.png");
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string ProgressDetail { get => _detail; set => Set(ref _detail, value); }
    public bool HasError { get => _error; set => Set(ref _error, value); }
    public bool HasWriteFailure
    {
        get => _writeFailed;
        set
        {
            if (!Set(ref _writeFailed, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateImage));
        }
    }
    public void NoteDeviceRemoval()
    {
        if (!IsBusy && Disk is not null && HasWriteFailure) _removalNotificationPending = true;
    }
    public void Report(OperationProgress update)
    {
        Progress = update.Percent;
        Stage = update.Stage;
        ProgressDetail = string.Join(" · ", new[] { update.TotalBytes > 0 ? $"{update.Percent:0.0}%" : "", update.SpeedText, update.EtaText.Length > 0 ? "剩余 " + update.EtaText : "", update.Detail ?? "" }.Where(x => x.Length > 0));
    }
}
