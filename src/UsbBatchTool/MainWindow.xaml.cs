using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using UsbBatchTool.Services;
using UsbBatchTool.Views;

namespace UsbBatchTool;

public partial class MainWindow : Window
{
    private readonly DeviceScanner _scanner = new();
    private readonly SettingsStore _store = new();
    private readonly CacheService _cache = new();
    private readonly LogService _log = new();
    private readonly PortDiscoveryTracker _portDiscovery = new();
    private readonly ObservableCollection<PortViewModel> _ports = [];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private AppSettings _settings;
    private readonly bool _demo;
    private IReadOnlyList<UsbDisk> _disks = [];
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _hotplugTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _refreshing, _busy, _switching, _identifying, _settingsOpen, _prompting;
    private bool _scanFailureLogged;
    private CancellationTokenSource? _cancellation;
    private string _selectedPath = "";
    private int _successful, _failed, _cancelled;

    private FrameworkElement _layout = null!;
    private Button ChooseFileButton = null!, ClearCacheButton = null!, SettingsButton = null!, WizardButton = null!, ManualBindButton = null!, RefreshButton = null!, SelectAllButton = null!, SelectNoneButton = null!, StartButton = null!, CancelButton = null!, ClearLogButton = null!, ExportLogButton = null!;
    private RadioButton ImageModeButton = null!, ZipModeButton = null!;
    private ComboBox RecentFilesBox = null!, FileSystemBox = null!, ConflictBox = null!;
    private TextBlock AccessText = null!, SourceSizeText = null!, ModeHintText = null!, HashText = null!, SelectionText = null!, UnboundText = null!, OverallText = null!, TaskSummaryText = null!;
    private TextBox FilePathBox = null!;
    private StackPanel ZipOptionsPanel = null!;
    private CheckBox FormatCheckBox = null!;
    private ItemsControl PortsItems = null!;
    private ListBox LogList = null!;
    private ProgressBar OverallProgress = null!;
    private bool IsZip => ZipModeButton.IsChecked == true;

    public MainWindow(bool demo = false)
    {
        _demo = demo;
        _settings = _store.Load();
        InitializeComponent();
        Title = "USB 批量烧录工具 v1.0.1" + (demo ? " · 演示模式" : "");
        Width = Math.Min(1300, SystemParameters.WorkArea.Width - 16);
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 16);
        MinWidth = Math.Min(1060, Width);
        MinHeight = Math.Min(710, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = EmbeddedAssets.Icon("app.ico");
        TaskbarItemInfo = new TaskbarItemInfo();
        PortsItems.ItemsSource = _ports;
        LogList.ItemsSource = _logs;
        _log.EntryAdded += entry => Dispatcher.BeginInvoke(() => { _logs.Add(entry); if (_logs.Count > 3000) _logs.RemoveAt(0); LogList.ScrollIntoView(entry); });
        WireEvents();
        _switching = true;
        FileSystemBox.ItemsSource = new[] { "exFAT", "NTFS", "FAT32" };
        FileSystemBox.SelectedItem = _settings.ZipFileSystem;
        if (FileSystemBox.SelectedIndex < 0) FileSystemBox.SelectedIndex = 0;
        ConflictBox.ItemsSource = new[] { "覆盖同名文件", "跳过同名文件", "逐个询问" };
        ConflictBox.SelectedIndex = _settings.ZipConflictPolicy switch { "Skip" => 1, "Ask" => 2, _ => 0 };
        FormatCheckBox.IsChecked = _settings.ZipFormatBeforeCopy;
        ZipModeButton.IsChecked = _settings.LastMode == "Zip";
        ImageModeButton.IsChecked = !IsZip;
        _switching = false;
        RebuildPorts();
        UpdateMode(false);
        _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _hotplugTimer.Tick += async (_, _) => { _hotplugTimer.Stop(); await RefreshDevicesAsync(); };
        SourceInitialized += (_, _) => { if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WindowMessage); };
        Loaded += async (_, _) =>
        {
            AccessText.Text = _demo ? "演示模式 · 禁止写入" : IsAdministrator() ? "管理员模式" : "权限不足 · 无法写入";
            _log.Write(_demo ? "演示模式已启动：只展示样例，不执行磁盘写入。" : "软件已启动。插入未绑定的 USB 磁盘后，可按提示添加端口。" );
            if (_store.LoadWarning is not null) _log.Write(_store.LoadWarning, level: "警告");
            await RefreshDevicesAsync();
            _refreshTimer.Start();
        };
        Closing += OnClosing;
    }

    public void InitializeComponent()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbBatchTool.MainWindow.xaml") ?? throw new FileNotFoundException("缺少主界面资源");
        _layout = (FrameworkElement)XamlReader.Load(stream); Content = _layout;
        T Find<T>(string name) where T : FrameworkElement => (T)(_layout.FindName(name) ?? throw new InvalidDataException("界面控件缺失：" + name));
        ChooseFileButton = Find<Button>(nameof(ChooseFileButton)); ClearCacheButton = Find<Button>(nameof(ClearCacheButton)); SettingsButton = Find<Button>(nameof(SettingsButton)); WizardButton = Find<Button>(nameof(WizardButton)); ManualBindButton = Find<Button>(nameof(ManualBindButton)); RefreshButton = Find<Button>(nameof(RefreshButton)); SelectAllButton = Find<Button>(nameof(SelectAllButton)); SelectNoneButton = Find<Button>(nameof(SelectNoneButton)); StartButton = Find<Button>(nameof(StartButton)); CancelButton = Find<Button>(nameof(CancelButton)); ClearLogButton = Find<Button>(nameof(ClearLogButton)); ExportLogButton = Find<Button>(nameof(ExportLogButton));
        ImageModeButton = Find<RadioButton>(nameof(ImageModeButton)); ZipModeButton = Find<RadioButton>(nameof(ZipModeButton)); RecentFilesBox = Find<ComboBox>(nameof(RecentFilesBox)); FileSystemBox = Find<ComboBox>(nameof(FileSystemBox)); ConflictBox = Find<ComboBox>(nameof(ConflictBox));
        AccessText = Find<TextBlock>(nameof(AccessText)); SourceSizeText = Find<TextBlock>(nameof(SourceSizeText)); ModeHintText = Find<TextBlock>(nameof(ModeHintText)); HashText = Find<TextBlock>(nameof(HashText)); SelectionText = Find<TextBlock>(nameof(SelectionText)); UnboundText = Find<TextBlock>(nameof(UnboundText)); OverallText = Find<TextBlock>(nameof(OverallText)); TaskSummaryText = Find<TextBlock>(nameof(TaskSummaryText)); FilePathBox = Find<TextBox>(nameof(FilePathBox)); ZipOptionsPanel = Find<StackPanel>(nameof(ZipOptionsPanel)); FormatCheckBox = Find<CheckBox>(nameof(FormatCheckBox)); PortsItems = Find<ItemsControl>(nameof(PortsItems)); LogList = Find<ListBox>(nameof(LogList)); OverallProgress = Find<ProgressBar>(nameof(OverallProgress));
    }

    private void WireEvents()
    {
        ChooseFileButton.Click += (_, _) => ChooseFile();
        ClearCacheButton.Click += (_, _) => ClearCache();
        SettingsButton.Click += (_, _) => OpenSettings(); ManualBindButton.Click += (_, _) => OpenSettings();
        WizardButton.Click += (_, _) => OpenWizard();
        RefreshButton.Click += async (_, _) => await RefreshDevicesAsync(true);
        SelectAllButton.Click += (_, _) => { foreach (var p in _ports.Where(p => p.CanSelect)) p.IsSelected = true; };
        SelectNoneButton.Click += (_, _) => { foreach (var p in _ports) p.IsSelected = false; };
        StartButton.Click += async (_, _) => await RunBatchAsync();
        CancelButton.Click += (_, _) => { _cancellation?.Cancel(); CancelButton.IsEnabled = false; OverallText.Text = "正在取消，等待当前操作结束…"; _log.Write("用户请求取消；格式化阶段将等待系统操作结束，未完成设备不能使用。", level: "警告"); };
        ClearLogButton.Click += (_, _) => _logs.Clear();
        ExportLogButton.Click += (_, _) => ExportLog();
        ImageModeButton.Checked += (_, _) => { if (!_switching) UpdateMode(true); };
        ZipModeButton.Checked += (_, _) => { if (!_switching) UpdateMode(true); };
        RecentFilesBox.SelectionChanged += (_, _) => { if (!_switching && RecentFilesBox.SelectedItem is RecentFile file) SetSelectedFile(file.Path); };
        FormatCheckBox.Checked += (_, _) => SaveOptions(); FormatCheckBox.Unchecked += (_, _) => SaveOptions();
        FileSystemBox.SelectionChanged += (_, _) => SaveOptions(); ConflictBox.SelectionChanged += (_, _) => SaveOptions();
    }

    private void RebuildPorts()
    {
        _ports.Clear();
        foreach (var binding in _settings.Ports.OrderBy(p => p.Order))
        {
            var port = new PortViewModel(binding);
            port.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(PortViewModel.IsSelected) or nameof(PortViewModel.Disk)) UpdateSelection(); };
            _ports.Add(port);
        }
        if (_ports.Count == 0)
        {
            UnboundText.Visibility = Visibility.Visible;
            UnboundText.Text = "当前 0 个端口。将 U 盘插入电脑或集线器任意接口，检测到后可按提示添加为端口 1。";
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _ports.Where(p => p.IsSelected && p.Disk is not null).ToList();
        SelectionText.Text = $"已选 {selected.Count} 台 · {DisplayBytes.Format(selected.Sum(p => p.Disk!.SizeBytes))}";
    }

    private void UpdateMode(bool save)
    {
        ZipOptionsPanel.Visibility = IsZip ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Content = IsZip ? "一键分发文件" : "一键烧录";
        ModeHintText.Text = IsZip ? "文件将复制到 U 盘根目录；启用重建并格式化会清除全部分区和数据。" : "镜像写入会清除目标 U 盘所有分区；支持重复烧录、无盘符及 Linux 分区。ISO 需支持 USB 原始写入才能启动。";
        _settings.LastMode = IsZip ? "Zip" : "Image";
        SetSelectedFile(IsZip ? _settings.LastZipPath : _settings.LastImagePath, false);
        RefreshRecentFiles();
        if (save) SaveSettings();
    }

    private void ChooseFile()
    {
        var dialog = new OpenFileDialog { Title = IsZip ? "选择 ZIP 文件" : "选择系统镜像", Filter = IsZip ? "ZIP 压缩包|*.zip" : "系统镜像|*.img;*.img.gz;*.iso", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SetSelectedFile(dialog.FileName);
    }

    private void SetSelectedFile(string path, bool save = true)
    {
        _selectedPath = path;
        FilePathBox.Text = string.IsNullOrEmpty(path) ? "请选择系统镜像或 ZIP 压缩包" : path;
        var recent = _settings.RecentFiles.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            SourceSizeText.Text = DisplayBytes.Format(file.Length) + " · " + (IsZip ? "ZIP" : path.EndsWith(".img.gz", StringComparison.OrdinalIgnoreCase) ? "IMG.GZ" : file.Extension.ToUpperInvariant().TrimStart('.'));
            HashText.Text = recent?.Sha256.Length > 0 ? "SHA-256：" + recent.Sha256 : "开始任务时计算 SHA-256，检查并复用解压缓存。";
            if (save)
            {
                if (IsZip) _settings.LastZipPath = path; else _settings.LastImagePath = path;
                recent ??= new RecentFile { Path = path, Mode = IsZip ? "Zip" : "Image" };
                _settings.RecentFiles.Remove(recent); recent.SizeBytes = file.Length; recent.LastUsed = DateTime.Now; _settings.RecentFiles.Insert(0, recent);
                _settings.RecentFiles = _settings.RecentFiles.Take(20).ToList();
            }
        }
        else
        {
            SourceSizeText.Text = string.IsNullOrEmpty(path) ? (IsZip ? "ZIP" : "IMG / IMG.GZ / ISO") : "文件不存在";
            HashText.Text = string.IsNullOrEmpty(path) ? "选择记录会自动保存；压缩文件按 SHA-256 复用解压缓存。" : "源文件已移动或删除，请重新选择。";
        }
        if (save) { SaveSettings(); RefreshRecentFiles(); }
    }

    private void RefreshRecentFiles()
    {
        bool previous = _switching; _switching = true;
        RecentFilesBox.ItemsSource = _settings.RecentFiles.Where(r => r.Mode == (IsZip ? "Zip" : "Image")).OrderByDescending(r => r.LastUsed).ToList();
        RecentFilesBox.SelectedItem = null; _switching = previous;
    }

    private async Task RefreshDevicesAsync(bool manual = false)
    {
        if (_refreshing || _identifying || _prompting) return;
        _refreshing = true;
        var scanSucceeded = false;
        try
        {
            _disks = _demo ? DemoDevices() : await _scanner.ScanAsync();
            scanSucceeded = true;
            _scanFailureLogged = false;
            var matched = new HashSet<int>();
            foreach (var port in _ports)
            {
                var candidates = _disks.Where(d => port.Binding.Matches(d)).ToList();
                UsbDisk? match = candidates.Count == 1 && matched.Add(candidates[0].Number) ? candidates[0] : null;
                if (port.IsBusy && port.Disk is not null && (match is null || port.Disk.IdentityKey != match.IdentityKey))
                {
                    port.HasError = true; port.Stage = "设备断开或重新枚举";
                }
                if (!port.IsBusy) port.Disk = match;
                if (candidates.Count > 1) { port.HasError = true; port.Stage = "绑定冲突，请重新识别"; }
            }
            var unbound = _disks.Where(d => !_settings.Ports.Any(p => p.Matches(d))).ToList();
            UnboundText.Visibility = _ports.Count == 0 || unbound.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UnboundText.Text = unbound.Count == 0
                ? "当前 0 个端口。将 U 盘插入电脑或集线器任意接口，检测到后可按提示添加为端口 1。"
                : $"发现 {unbound.Count} 台未绑定 USB 磁盘：{string.Join("；", unbound.Select(d => d.DisplayName))}。可按提示添加端口，或打开“管理端口”手动绑定。";
            if (manual)
            {
                _log.Write($"刷新完成：检测到 {_disks.Count} 台 USB 磁盘，未绑定 {unbound.Count} 台。");
                foreach (var disk in _disks.Where(d => !d.CanWrite))
                    _log.Write($"{disk.DisplayName}：可绑定 {(disk.CanBind ? "是" : "否")}；{disk.SafetyReason}", level: "警告");
            }
        }
        catch (Exception ex)
        {
            if (!_scanFailureLogged || manual) _log.Write("设备扫描失败：" + ex.Message, level: "错误");
            _scanFailureLogged = true;
            UnboundText.Visibility = Visibility.Visible;
            UnboundText.Text = "设备扫描失败：" + ex.Message + "。请点击“刷新设备”重试，详细原因见日志。";
            if (!_busy) foreach (var p in _ports) p.Disk = null;
        }
        finally { _refreshing = false; }
        if (scanSucceeded) await PromptForNewPortAsync();
    }

    private async Task PromptForNewPortAsync()
    {
        if (_prompting || _busy || _identifying || _settingsOpen || _demo || !IsLoaded || !IsVisible) return;
        var candidate = _portDiscovery.NextCandidate(_disks, _settings.Ports);
        if (candidate is null) return;
        _prompting = true;
        try
        {
            var order = _settings.Ports.Count + 1;
            var location = candidate.LocationPaths.Count > 0
                ? "将按 USB 物理位置绑定；以后换一只 U 盘插在此接口，也会显示在这个端口。"
                : "Windows 没有提供此设备的物理位置；将按设备身份绑定，更换 U 盘后需重新绑定。";
            var answer = MessageBox.Show(this,
                $"检测到未绑定的 USB 磁盘：\n{candidate.DisplayName}\n\n是否添加为端口 {order}？\n{location}",
                "添加 USB 端口", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            // A disk number is ephemeral. Confirm that the same device and USB
            // topology are still present immediately before persisting a binding.
            var freshDisks = await _scanner.ScanAsync();
            var matches = freshDisks.Where(d => d.IdentityKey == candidate.IdentityKey).ToList();
            if (matches.Count != 1)
            {
                _log.Write("添加端口已取消：设备已拔出或重新枚举。", level: "警告");
                return;
            }
            var binding = PortDiscoveryTracker.CreateBinding(matches[0], _settings.Ports);
            if (binding is null)
            {
                _log.Write("添加端口已取消：设备位置不可绑定，或该位置已有绑定。", level: "警告");
                return;
            }
            _settings.Ports.Add(binding);
            SaveSettings();
            RebuildPorts();
            _log.Write($"已添加 {binding.Name}：{matches[0].DisplayName}。", binding.Name);
            _ = Dispatcher.BeginInvoke(async () => await RefreshDevicesAsync());
        }
        catch (Exception ex)
        {
            _log.Write("添加端口失败：" + ex.Message, level: "错误");
        }
        finally
        {
            _prompting = false;
            // If several drives appeared together, offer them one at a time.
            _ = Dispatcher.BeginInvoke(async () => await PromptForNewPortAsync());
        }
    }

    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0219 && (wParam == 0x8000 || wParam == 0x8004 || wParam == 0x0007))
        {
            if (wParam == 0x8004) NoteDeviceRemoval(lParam);
            _hotplugTimer.Stop(); _hotplugTimer.Start();
        }
        return 0;
    }

    private void NoteDeviceRemoval(nint lParam)
    {
        // A fast unplug/replug may finish before the debounced scan. Keep the
        // removal event so the next scan clears a stale write-failure state.
        int removedDriveMask = 0;
        bool volumeEvent = false;
        if (lParam != 0)
        {
            try
            {
                volumeEvent = Marshal.ReadInt32(lParam, 4) == 2; // DBT_DEVTYP_VOLUME
                if (volumeEvent) removedDriveMask = Marshal.ReadInt32(lParam, 12);
            }
            catch (AccessViolationException) { return; }
        }
        foreach (var port in _ports)
        {
            if (volumeEvent && (port.Disk is null || !port.Disk.Volumes.SelectMany(v => v.MountPoints).Any(path =>
                path.Length >= 2 && path[1] == ':' && char.ToUpperInvariant(path[0]) is >= 'A' and <= 'Z' &&
                (removedDriveMask & (1 << (char.ToUpperInvariant(path[0]) - 'A'))) != 0))) continue;
            port.NoteDeviceRemoval();
        }
    }

    private void OpenSettings()
    {
        if (_busy) return;
        _settingsOpen = true;
        try
        {
            var dialog = new PortSettingsWindow(_settings, _disks) { Owner = this };
            if (dialog.ShowDialog() == true) ApplySettings(dialog.ResultSettings);
        }
        finally { _settingsOpen = false; _ = RefreshDevicesAsync(); }
    }

    private void OpenWizard()
    {
        if (_busy || _demo) { if (_demo) MessageBox.Show(this, "演示模式不执行设备识别。", "提示"); return; }
        _identifying = true;
        try
        {
            var dialog = new BindingWizardWindow(_settings, () => _scanner.ScanAsync()) { Owner = this };
            if (dialog.ShowDialog() == true) ApplySettings(dialog.ResultSettings);
        }
        finally { _identifying = false; _ = RefreshDevicesAsync(); }
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings; SaveSettings(); RebuildPorts();
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.RefreshSeconds, 2, 60));
        _switching = true; FormatCheckBox.IsChecked = settings.ZipFormatBeforeCopy; FileSystemBox.SelectedItem = settings.ZipFileSystem; ConflictBox.SelectedIndex = settings.ZipConflictPolicy switch { "Skip" => 1, "Ask" => 2, _ => 0 }; _switching = false;
        _log.Write("端口和运行设置已保存。"); _ = RefreshDevicesAsync();
    }

    private void SaveOptions()
    {
        if (_switching) return;
        _settings.ZipFormatBeforeCopy = FormatCheckBox.IsChecked == true;
        _settings.ZipFileSystem = FileSystemBox.SelectedItem as string ?? "exFAT";
        _settings.ZipConflictPolicy = ConflictBox.SelectedIndex switch { 1 => "Skip", 2 => "Ask", _ => "Overwrite" };
        SaveSettings();
    }
    private void SaveSettings() { if (!_demo) _store.Save(_settings); }

    private void ClearCache()
    {
        if (_busy) return;
        if (MessageBox.Show(this, "清除全部文件选择记录及 cache 内的解压缓存？\n端口绑定和原始文件会保留。", "清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _cache.Clear(); _settings.RecentFiles.Clear(); _settings.LastImagePath = _settings.LastZipPath = ""; SetSelectedFile("", false); RefreshRecentFiles(); SaveSettings(); _log.Write("已清除解压缓存和文件选择记录。"); }
        catch (Exception ex) { ShowError("缓存清理失败：" + ex.Message); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var button in new[] { ChooseFileButton, ClearCacheButton, SettingsButton, WizardButton, ManualBindButton, SelectAllButton, SelectNoneButton, StartButton }) button.IsEnabled = !busy;
        ImageModeButton.IsEnabled = ZipModeButton.IsEnabled = RecentFilesBox.IsEnabled = FormatCheckBox.IsEnabled = FileSystemBox.IsEnabled = ConflictBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        TaskbarItemInfo.ProgressState = busy ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;
    }

    private async Task RunBatchAsync()
    {
        if (_busy) return;
        if (_demo) { MessageBox.Show(this, "演示模式用于查看界面，禁止写入真实设备。", "提示"); return; }
        if (!IsAdministrator()) { ShowError("请以管理员身份运行此软件后再开始任务。"); return; }
        if (!File.Exists(_selectedPath)) { ShowError("请先选择存在的镜像或 ZIP 文件。"); return; }
        await RefreshDevicesAsync();
        var selected = _ports.Where(p => p.IsSelected && p.Disk?.CanWrite == true).ToList();
        if (selected.Count == 0) { ShowError("请先绑定端口，并选择至少一个可安全写入的 USB 磁盘。"); return; }
        if (selected.Select(p => p.Disk!.Number).Distinct().Count() != selected.Count) { ShowError("同一设备被重复绑定，请先修正端口设置。"); return; }
        bool zip = IsZip, format = _settings.ZipFormatBeforeCopy;
        string source = _selectedPath;
        string fileSystem = _settings.ZipFileSystem, conflict = _settings.ZipConflictPolicy;
        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;
        SetBusy(true); _successful = _failed = _cancelled = 0;
        _log.BeginTask();
        try
        {
            foreach (var p in selected) { p.Progress = 0; p.HasError = false; p.HasWriteFailure = false; p.Stage = "等待准备"; p.ProgressDetail = ""; p.IsBusy = true; }
            _log.Write("准备源文件：" + source);
            var preparation = new Progress<OperationProgress>(p => { OverallText.Text = $"{p.Stage} · {p.Percent:0.0}%  {p.Detail}"; OverallProgress.Value = p.Percent; TaskbarItemInfo.ProgressValue = p.Percent / 100; });
            PreparedSource prepared = await _cache.PrepareAsync(source, zip, preparation, ct);
            var recent = _settings.RecentFiles.FirstOrDefault(r => r.Path == source);
            if (recent is not null) { recent.Sha256 = prepared.Sha256; recent.CachePath = prepared.PayloadPath; recent.LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(source).Ticks; recent.LastUsed = DateTime.Now; SaveSettings(); }
            HashText.Text = $"SHA-256：{prepared.Sha256} · {(prepared.CacheHit ? "已复用缓存" : "源文件已就绪")}";
            _log.Write($"源文件就绪：{DisplayBytes.Format(prepared.TotalBytes)}，SHA-256 {prepared.Sha256}，{(prepared.CacheHit ? "复用解压缓存" : "完成准备")}");
            // Keep the original source open until every queued job finishes. FileShare.Read
            // prevents a ZIP/IMG.GZ from being moved onto a target disk after confirmation.
            using var originalSourceLease = new FileStream(prepared.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var port in selected)
            {
                PhysicalPathGuard.AssertNotOnTarget(port.Disk!, prepared.OriginalPath);
                PhysicalPathGuard.AssertNotOnTarget(port.Disk!, prepared.PayloadPath);
            }
            if (!zip && selected.Any(p => p.Disk!.SizeBytes < prepared.TotalBytes)) throw new IOException("存在容量小于镜像的目标设备，请取消勾选后重试。");
            var snapshots = selected.ToDictionary(p => p.Id, p => p.Disk!);
            string targets = string.Join("\n", selected.Select(p => $"{p.Name} → 磁盘 {p.Disk!.Number} · {p.Disk.Model} · {p.Disk.CapacityText}\n  序列号：{(p.Disk.SerialNumber.Length > 0 ? p.Disk.SerialNumber : "未提供")} · {p.Disk.DriveLetters}"));
            string impact = !zip ? "将清除所选 U 盘全部分区和数据，并从头写入镜像。已烧录过的 U 盘也会清理并重新写入。" : format ? $"将清除全部旧分区，重建 {fileSystem} 文件盘后复制 ZIP 内容。" : "将文件复制到目标根目录。已有文件按当前覆盖策略处理。";
            if (!ConfirmTargets(source, targets, impact)) { _log.Write("用户取消确认，未写入设备。"); return; }
            ct.ThrowIfCancellationRequested();
            OverallProgress.Value = 0;
            using var semaphore = new SemaphoreSlim(Math.Clamp(_settings.ConcurrentJobs, 1, 7));
            var percentages = selected.ToDictionary(p => p.Id, _ => 0.0);
            foreach (var p in selected) p.Stage = "排队等待";
            var jobs = selected.Select(async port =>
            {
                try
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var progress = new Progress<OperationProgress>(value =>
                        {
                            port.Report(value); percentages[port.Id] = value.Percent;
                            OverallProgress.Value = percentages.Values.Average();
                            TaskbarItemInfo.ProgressValue = OverallProgress.Value / 100;
                            OverallText.Text = $"总体进度 {OverallProgress.Value:0.0}% · 成功 {_successful} / 失败 {_failed} / 取消 {_cancelled}";
                        });
                        Action<string> log = message => _log.Write(message, port.Name);
                        var disk = snapshots[port.Id];
                        _log.Write($"任务开始：{disk.DevicePath}，{disk.Model}，{disk.CapacityText}，Serial={disk.SerialNumber}", port.Name);
                        if (zip) await new ZipCopyService().CopyAsync(disk, prepared.PayloadPath, format, fileSystem, conflict, ConfirmOverwriteAsync, progress, log, ct);
                        else await new RawDiskWriter().WriteAsync(disk, prepared.PayloadPath, _settings.VerifyAfterWrite, progress, log, ct);
                        port.Progress = 100; port.Stage = "已完成"; port.ProgressDetail = "设备任务完成"; _successful++;
                        _log.Write("任务成功。", port.Name);
                    }
                    finally { semaphore.Release(); }
                }
                catch (OperationCanceledException)
                {
                    port.Stage = "已取消"; port.ProgressDetail = "未完成数据不可使用，可重新烧录"; _cancelled++;
                    _log.Write("任务取消；目标可能包含未完成的数据。", port.Name, "警告");
                }
                catch (Exception ex)
                {
                    port.HasError = true; port.HasWriteFailure = true; port.Stage = "失败"; port.ProgressDetail = ex.Message; _failed++;
                    _log.Write(ex.Message, port.Name, "错误");
                }
                finally
                {
                    port.IsBusy = false;
                    percentages[port.Id] = 100;
                    OverallProgress.Value = percentages.Values.Average();
                    TaskSummaryText.Text = $"成功 {_successful} 台 · 失败 {_failed} 台 · 取消 {_cancelled} 台";
                }
            }).ToList();
            await Task.WhenAll(jobs);
            OverallText.Text = $"任务结束 · 成功 {_successful} 台 · 失败 {_failed} 台 · 取消 {_cancelled} 台";
            _log.Write(OverallText.Text);
            if (_failed > 0) TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error;
        }
        catch (OperationCanceledException) { OverallText.Text = "任务已取消"; _log.Write("源文件准备已取消，未写入设备。", level: "警告"); }
        catch (Exception ex) { OverallText.Text = "任务未完成"; ShowError(ex.Message); }
        finally
        {
            foreach (var p in selected) { p.IsBusy = false; if (p.Stage is "等待准备" or "排队等待") p.Stage = "未开始"; }
            SetBusy(false); _cancellation.Dispose(); _cancellation = null; await RefreshDevicesAsync();
        }
    }

    private bool ConfirmTargets(string source, string targets, string impact)
    {
        var window = new Window { Owner = this, Title = "确认批量操作", Width = 660, Height = 590, MinHeight = 450, ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon };
        var panel = new DockPanel { Margin = new Thickness(22) };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var ok = new Button { Content = "确认开始", IsEnabled = false, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => window.DialogResult = true;
        bottom.Children.Add(cancel); bottom.Children.Add(ok); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var check = new CheckBox { Content = "我已核对源文件和全部目标设备，确认执行以上操作。", Margin = new Thickness(0, 15, 0, 0) };
        check.Checked += (_, _) => ok.IsEnabled = true; check.Unchecked += (_, _) => ok.IsEnabled = false;
        DockPanel.SetDock(check, Dock.Bottom); panel.Children.Add(check);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = impact, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 16) });
        content.Children.Add(new TextBlock { Text = "源文件：\n" + source + "\n\n目标设备：\n" + targets, TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = panel;
        return window.ShowDialog() == true;
    }

    private async Task<bool> ConfirmOverwriteAsync(string name) => await Dispatcher.InvokeAsync(() => MessageBox.Show(this, "覆盖目标同名文件？\n" + name, "文件冲突", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    private void ExportLog()
    {
        var dialog = new SaveFileDialog { Title = "导出当前日志", FileName = $"USB任务日志_{DateTime.Now:yyyyMMdd_HHmmss}.log", Filter = "日志文件|*.log" };
        if (dialog.ShowDialog(this) == true) { File.WriteAllLines(dialog.FileName, _logs.Select(l => l.ToString())); _log.Write("日志已导出：" + dialog.FileName); }
    }
    private void ShowError(string message) { _log.Write(message, level: "错误"); MessageBox.Show(this, message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; MessageBox.Show(this, "任务仍在运行。请先点击“取消任务”，等待结束后再关闭。", "任务运行中", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _refreshTimer.Stop(); _hotplugTimer.Stop(); SaveSettings();
    }
    private static bool IsAdministrator() { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }

    private IReadOnlyList<UsbDisk> DemoDevices()
    {
        var output = new List<UsbDisk>();
        for (int i = 0; i < _ports.Count; i++)
        {
            var port = _ports[i]; port.Binding.LocationPaths = [$"DEMO_PORT_{i}"];
            if (i is 2 or 5) continue;
            output.Add(new UsbDisk { Number = i + 1, Model = i % 2 == 0 ? "SanDisk Ultra USB 3.0" : "Kingston DataTraveler", SerialNumber = "DEMO" + i, PnpDeviceId = "DEMO" + i, LocationPaths = [$"DEMO_PORT_{i}"], SizeBytes = i % 2 == 0 ? 32000000000 : 64000000000, BusType = 7, IsRemovable = true, SafetyVerified = true, Volumes = [new VolumeInfo { VolumePath = "DEMO", MountPoints = [$"{(char)('E' + i)}:\\"], Label = "USB_DATA", FileSystem = "exFAT", SizeBytes = 32000000000, FreeBytes = 27000000000, IsReady = true }] });
        }
        return output;
    }
}
