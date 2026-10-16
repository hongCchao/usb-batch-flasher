using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsbBatchTool.Views;

/// <summary>
/// 按端口逐步识别 USB 设备的向导。扫描到的设备以 Location Path 绑定到端口，
/// 因此更换 U 盘后仍可匹配同一物理端口；序列号不会被用作端口匹配条件。
/// </summary>
public sealed class BindingWizardWindow : Window
{
    private readonly AppSettings _working;
    private readonly Func<Task<IReadOnlyList<UsbDisk>>> _scan;
    private readonly ListBox _candidateList = new();
    private readonly TextBlock _stepText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _detailsText = new();
    private readonly TextBlock _mappingText = new();
    private readonly Button _scanButton;
    private readonly Button _confirmButton;
    private readonly Button _aliasButton;
    private readonly Button _skipButton;
    private readonly Button _previousButton;
    private readonly Button _addPortButton;
    private readonly Button _finishButton;
    private readonly List<UsbDisk> _baseline = [];
    private List<UsbDisk> _candidates = [];
    private List<UsbDisk> _currentSnapshot = [];
    private int _stepIndex;
    private bool _isScanning;
    private bool _initialized;
    private bool _hasBaseline;
    private bool _hasNewCandidates;
    private readonly HashSet<Guid> _changedPortIds = [];
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closed;
    private bool _addingPorts;

    public AppSettings ResultSettings { get; private set; }

    public BindingWizardWindow(AppSettings settings, Func<Task<IReadOnlyList<UsbDisk>>> scan)
    {
        _working = Clone(settings);
        EnsurePorts(_working);
        _addingPorts = _working.Ports.Count == 0;
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        ResultSettings = Clone(_working);

        Title = "USB 端口识别向导";
        Width = 850;
        Height = 600;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _scanButton = MakeButton("重新扫描", async (_, _) => await ScanCurrentAsync());
        _confirmButton = MakeButton("确认绑定并下一步", ConfirmBinding);
        _aliasButton = MakeButton("追加 USB2/3 位置别名", AppendAlias);
        _skipButton = MakeButton("跳过此端口", SkipPort);
        _previousButton = MakeButton("上一步", PreviousPort);
        _addPortButton = MakeButton("新增端口", StartAddingPorts);
        _finishButton = MakeButton("保存并完成", Finish);

        BuildUi();
        Loaded += async (_, _) => await InitializeAsync();
        _monitorTimer.Tick += async (_, _) => await ScanCurrentAsync(silent: true);
        Closed += (_, _) => { _closed = true; _monitorTimer.Stop(); };
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = "识别电脑或集线器的 USB 端口",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 43, 77))
        });
        heading.Children.Add(new TextBlock
        {
            Text = "向导会先建立设备基线，再检测新插入的 U 盘。请按提示逐个插入目标端口；如果 Windows 未提供稳定物理端口号，可在这里追加 USB 2.0/3.0 位置路径别名。",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139)),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var stepPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(235, 242, 253)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 0, 0, 12)
        };
        _stepText.FontWeight = FontWeights.SemiBold;
        _stepText.Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        stepPanel.Child = _stepText;
        Grid.SetRow(stepPanel, 1);
        root.Children.Add(stepPanel);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid { Margin = new Thickness(0, 0, 12, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(new TextBlock { Text = "本次扫描发现的候选设备", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        _candidateList.BorderBrush = new SolidColorBrush(Color.FromRgb(214, 223, 236));
        _candidateList.DisplayMemberPath = nameof(UsbDisk.DisplayName);
        _candidateList.SelectionChanged += (_, _) => UpdateCandidateDetails();
        Grid.SetRow(_candidateList, 1);
        left.Children.Add(_candidateList);
        _statusText.Margin = new Thickness(0, 9, 0, 0);
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139));
        _statusText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_statusText, 2);
        left.Children.Add(_statusText);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(new TextBlock { Text = "设备信息", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        _detailsText.TextWrapping = TextWrapping.Wrap;
        _detailsText.Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139));
        Grid.SetRow(_detailsText, 1);
        right.Children.Add(_detailsText);
        var guide = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 253)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 231, 242)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 12)
        };
        _mappingText.TextWrapping = TextWrapping.Wrap;
        guide.Child = new ScrollViewer { Content = _mappingText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(guide, 2);
        right.Children.Add(guide);
        var scanLine = new StackPanel { Orientation = Orientation.Horizontal };
        scanLine.Children.Add(_scanButton);
        Grid.SetRow(scanLine, 3);
        right.Children.Add(scanLine);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bindingActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bindingActions.Children.Add(_previousButton);
        bindingActions.Children.Add(_skipButton);
        bindingActions.Children.Add(_aliasButton);
        bindingActions.Children.Add(_addPortButton);
        bindingActions.Children.Add(_confirmButton);
        footer.Children.Add(bindingActions);
        var endActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", (_, _) => { DialogResult = false; Close(); });
        endActions.Children.Add(_finishButton);
        endActions.Children.Add(cancel);
        Grid.SetRow(endActions, 1);
        footer.Children.Add(endActions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
    }

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _stepIndex = 0;
        await ScanCurrentAsync(captureBaseline: true);
        if (!_closed) _monitorTimer.Start();
    }

    private async Task ScanCurrentAsync(bool captureBaseline = false, bool silent = false)
    {
        if (_isScanning || _closed) return;
        _isScanning = true;
        UpdateButtons();
        if (!silent) _statusText.Text = "正在扫描 USB 设备，请稍候……";
        try
        {
            var found = await _scan();
            if (_closed) return;
            _currentSnapshot = found?.Where(d => d is not null).ToList() ?? [];
            if (captureBaseline || !_hasBaseline)
            {
                _baseline.Clear();
                _baseline.AddRange(_currentSnapshot);
                _hasBaseline = true;
            }
            var candidates = FindCandidates(_currentSnapshot);
            var hasNewCandidates = candidates.Count > 0;
            if (!hasNewCandidates) candidates = _currentSnapshot.ToList();
            var previousKeys = _candidates.Select(DeviceKey).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var nextKeys = candidates.Select(DeviceKey).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            if (silent && previousKeys.SequenceEqual(nextKeys, StringComparer.OrdinalIgnoreCase)) return;
            var selectedKey = SelectedDisk is null ? null : DeviceKey(SelectedDisk);
            _candidates = candidates;
            _hasNewCandidates = hasNewCandidates;
            if (!_hasNewCandidates)
            {
                _statusText.Text = _currentSnapshot.Count == 0
                    ? "没有检测到 USB 磁盘，请插入设备后重新扫描。"
                    : "未发现基线外的新设备，以下列出当前设备，可手动选择并确认绑定。";
            }
            else
            {
                _statusText.Text = _addingPorts
                    ? $"发现 {_candidates.Count} 个新设备，请选择并添加为端口 {_working.Ports.Count + 1}。"
                    : $"发现 {_candidates.Count} 个新设备，请选择当前端口对应的设备。";
            }
            if (captureBaseline)
                _statusText.Text = "已建立当前设备基线。请在提示的端口插入 U 盘后重新扫描；也可以手动选择列表中已插入的设备。";
            _candidateList.ItemsSource = null;
            _candidateList.ItemsSource = _candidates;
            var previousSelection = selectedKey is null ? null : _candidates.FirstOrDefault(d => DeviceKey(d).Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
            if (previousSelection is not null) _candidateList.SelectedItem = previousSelection;
            else if (_candidates.Count == 1) _candidateList.SelectedIndex = 0;
            UpdateStepText();
            UpdateCandidateDetails();
        }
        catch (Exception ex)
        {
            _statusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _isScanning = false;
            UpdateButtons();
        }
    }

    private List<UsbDisk> FindCandidates(IReadOnlyList<UsbDisk> snapshot)
    {
        if (_baseline.Count == 0) return snapshot.ToList();
        var baselineKeys = _baseline.Select(DeviceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Where(d => !baselineKeys.Contains(DeviceKey(d))).ToList();
    }

    private void ConfirmBinding(object? sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk;
        if (_addingPorts)
        {
            AddDetectedPort(disk);
            return;
        }
        var port = CurrentPort;
        if (port is null || disk is null)
        {
            _statusText.Text = "请先扫描并选择一个设备。";
            return;
        }
        if (!ValidateBindingTarget(port, disk)) return;
        if (!_hasNewCandidates && MessageBox.Show(this,
                $"此设备已存在于扫描基线中。请确认 {disk.DisplayName} 确实连接在“{port.Name}”，是否继续绑定？",
                "确认端口位置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        // 此步骤已追加 USB2/3 别名时，下一步保留同一端口的全部别名。
        if (!_changedPortIds.Contains(port.Id))
            port.LocationPaths = [];
        port.FallbackDeviceKey = disk.LocationPaths.Count == 0 ? disk.PersistentDeviceKey : "";
        foreach (var path in disk.LocationPaths.Where(p => !port.LocationPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            port.LocationPaths.Add(path);
        _changedPortIds.Add(port.Id);
        port.LastModel = disk.Model;
        port.LastSerial = disk.SerialNumber;
        _statusText.Text = disk.LocationPaths.Count == 0 ? $"已按设备身份绑定 {port.Name}；更换 U 盘后须重新手动绑定。" : $"已将 {disk.DisplayName} 绑定为 {port.Name}。";
        Advance();
    }

    private void StartAddingPorts(object? sender, RoutedEventArgs e)
    {
        if (_working.Ports.Count >= 32)
        {
            _statusText.Text = "最多可配置 32 个端口。";
            return;
        }
        _addingPorts = true;
        _baseline.Clear();
        _baseline.AddRange(_currentSnapshot);
        _hasBaseline = true;
        UpdateStepText();
        UpdateButtons();
        _statusText.Text = $"请将 U 盘插入尚未绑定的物理接口，发现后点击“添加为端口 {_working.Ports.Count + 1}”。也可选择已插入的设备。";
    }

    private void AddDetectedPort(UsbDisk? disk)
    {
        if (disk is null)
        {
            _statusText.Text = "请先扫描并选择一个 USB 磁盘。";
            return;
        }
        if (_working.Ports.Count >= 32)
        {
            _statusText.Text = "最多可配置 32 个端口。";
            return;
        }
        if (!disk.CanBind)
        {
            _statusText.Text = "该设备不是可绑定的 USB 位置，或属于系统/启动磁盘。";
            return;
        }
        if (_working.Ports.Any(p => p.Matches(disk)))
        {
            _statusText.Text = "此物理位置或设备已绑定，请换到尚未绑定的 USB 接口。";
            return;
        }
        if (!_hasNewCandidates && MessageBox.Show(this,
                $"此设备已存在于扫描基线中。确认将 {disk.DisplayName} 当前所在位置添加为端口 {_working.Ports.Count + 1}？",
                "确认端口位置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var order = _working.Ports.Count + 1;
        _working.Ports.Add(new PortBinding
        {
            Order = order,
            Name = $"端口 {order}",
            LocationPaths = disk.LocationPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FallbackDeviceKey = disk.LocationPaths.Count == 0 ? disk.PersistentDeviceKey : "",
            LastModel = disk.Model,
            LastSerial = disk.SerialNumber
        });
        _baseline.Clear();
        _baseline.AddRange(_currentSnapshot);
        _hasBaseline = true;
        _candidates.Clear();
        _candidateList.ItemsSource = null;
        _hasNewCandidates = false;
        _detailsText.Text = "请将 U 盘插入下一个接口，或点击“保存并完成”。";
        UpdateStepText();
        UpdateButtons();
        _statusText.Text = $"端口 {order} 已绑定。请插入下一个接口，扫描后继续添加；也可以保存并完成。";
    }

    private void AppendAlias(object? sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk;
        var port = CurrentPort;
        if (port is null || disk is null)
        {
            _statusText.Text = "请先扫描并选择一个设备。";
            return;
        }
        if (disk.LocationPaths.Count == 0)
        {
            _statusText.Text = "所选设备没有可追加的 Location Path。";
            return;
        }
        if (!ValidateBindingTarget(port, disk)) return;
        if (MessageBox.Show(this,
                $"请确认选中的 {disk.DisplayName} 连接在“{port.Name}”同一个物理插口。\nUSB 2.0 与 USB 3.0 设备可能走不同拓扑路径，只有确认属于同一插口时才应追加。",
                "确认位置别名", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        port.LocationPaths ??= [];
        port.FallbackDeviceKey = "";
        foreach (var path in disk.LocationPaths.Where(p => !port.LocationPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            port.LocationPaths.Add(path);
        port.LastModel = disk.Model;
        port.LastSerial = disk.SerialNumber;
        _changedPortIds.Add(port.Id);
        _statusText.Text = $"已为 {port.Name} 追加位置别名。可继续扫描同一端口的另一条 USB 2.0/3.0 路径，或点击“确认绑定并下一步”。";
        UpdateStepText();
    }

    private void SkipPort(object? sender, RoutedEventArgs e)
    {
        if (CurrentPort is not null) _statusText.Text = $"已跳过 {CurrentPort.Name}。";
        Advance();
    }

    private void PreviousPort(object? sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0) return;
        _stepIndex--;
        UpdateStepText();
        _candidateList.SelectedItem = null;
        _statusText.Text = "已返回上一个端口，原绑定保留。可为该物理端口追加路径别名；如需替换全部旧路径，请使用端口设置中的绑定功能。";
        UpdateButtons();
    }

    private void Advance()
    {
        if (_stepIndex >= _working.Ports.Count - 1)
        {
            UpdateStepText();
            _statusText.Text = "最后一个端口已处理。请检查绑定列表，再点击“保存并完成”；如需 USB 2.0/3.0 别名，可继续追加。";
            _finishButton.Focus();
            return;
        }
        _baseline.Clear();
        _baseline.AddRange(_currentSnapshot);
        _hasBaseline = true;
        _stepIndex++;
        UpdateStepText();
        _candidateList.ItemsSource = null;
        _candidates.Clear();
        _hasNewCandidates = false;
        _detailsText.Text = "请将设备插入提示的端口，然后点击“重新扫描”。";
        _statusText.Text = "当前设备基线已更新。可以使用同一个 U 盘依次移动到各插口，也可逐个插入不同 U 盘。";
        UpdateButtons();
    }

    private void Finish(object? sender, RoutedEventArgs? e)
    {
        if (_isScanning) return;
        var unbound = _working.Ports.Count(p => p.LocationPaths.Count == 0 && p.FallbackDeviceKey.Length == 0);
        if (unbound > 0 && MessageBox.Show(this, $"还有 {unbound} 个端口未绑定，是否保存当前结果？", "保存端口绑定", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        ResultSettings = Clone(_working);
        DialogResult = true;
        Close();
    }

    private void UpdateStepText()
    {
        if (_addingPorts)
        {
            _stepText.Text = $"准备添加端口 {_working.Ports.Count + 1} · 已绑定 {_working.Ports.Count} 个端口";
            _mappingText.Text = _working.Ports.Count == 0
                ? "尚未绑定端口。插入任意电脑或集线器接口上的 U 盘，发现设备后即可添加为端口 1。"
                : string.Join("\n", _working.Ports.Select(p => $"{p.Order}. {p.Name}：{(p.LocationPaths.Count > 0 ? $"已绑定 {p.LocationPaths.Count} 个位置别名" : p.FallbackDeviceKey.Length > 0 ? "设备身份绑定" : "待识别")}"));
            return;
        }
        var port = CurrentPort;
        if (port is null)
        {
            _stepText.Text = "识别完成";
            _mappingText.Text = "没有可识别的端口。请关闭向导后在端口设置中添加端口。";
            return;
        }
        _stepText.Text = $"步骤 {_stepIndex + 1}/{_working.Ports.Count}：请将设备插入 {port.Name}，然后重新扫描";
        var lines = _working.Ports.Select(p =>
        {
            var status = p.LocationPaths.Count == 0 ? p.FallbackDeviceKey.Length > 0 ? "设备身份绑定" : "待识别" : $"已绑定 {p.LocationPaths.Count} 个位置别名";
            return $"{p.Order}. {p.Name}：{status}";
        });
        _mappingText.Text = string.Join("\n", lines);
    }

    private void UpdateCandidateDetails()
    {
        var disk = SelectedDisk;
        if (disk is null)
        {
            _detailsText.Text = "请选择候选设备。";
            UpdateButtons();
            return;
        }
        var paths = disk.LocationPaths.Count == 0 ? "无 Location Path" : string.Join("\n", disk.LocationPaths);
        _detailsText.Text = $"设备：{disk.Model}\n物理磁盘：{disk.DevicePath}\n容量：{disk.CapacityText}\n序列号：{(string.IsNullOrWhiteSpace(disk.SerialNumber) ? "无" : disk.SerialNumber)}\n位置路径：\n{paths}";
        UpdateButtons();
    }

    private UsbDisk? SelectedDisk => _candidateList.SelectedItem as UsbDisk;
    private PortBinding? CurrentPort => _stepIndex >= 0 && _stepIndex < _working.Ports.Count ? _working.Ports[_stepIndex] : null;

    private void UpdateButtons()
    {
        _scanButton.IsEnabled = !_isScanning;
        _confirmButton.Content = _addingPorts ? $"添加为端口 {_working.Ports.Count + 1}" : "确认绑定并下一步";
        _confirmButton.IsEnabled = !_isScanning && (_addingPorts ? _working.Ports.Count < 32 : CurrentPort is not null) && SelectedDisk is not null;
        _aliasButton.IsEnabled = !_isScanning && !_addingPorts && CurrentPort is not null && SelectedDisk is not null;
        _skipButton.IsEnabled = !_isScanning && !_addingPorts && CurrentPort is not null;
        _previousButton.IsEnabled = !_isScanning && !_addingPorts && _stepIndex > 0;
        _addPortButton.IsEnabled = !_isScanning && !_addingPorts && _working.Ports.Count < 32;
        _finishButton.IsEnabled = !_isScanning;
    }

    private static string DeviceKey(UsbDisk disk)
    {
        // 仅用于判断本次扫描是否出现了新设备；最终端口绑定仍然只使用 LocationPaths。
        return $"{disk.PnpDeviceId}|{disk.SerialNumber}|{disk.SizeBytes}|{string.Join(";", disk.LocationPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))}";
    }

    private bool ValidateBindingTarget(PortBinding port, UsbDisk disk)
    {
        if (!disk.CanBind)
        {
            _statusText.Text = "该设备不是可绑定的 USB 位置，或属于系统/启动磁盘。";
            return false;
        }
        var other = _working.Ports.FirstOrDefault(p => p.Id != port.Id &&
            (p.LocationPaths.Any(path => disk.LocationPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) ||
             disk.LocationPaths.Count == 0 && p.FallbackDeviceKey == disk.PersistentDeviceKey));
        if (other is null) return true;
        _statusText.Text = $"该设备的路径已经绑定到“{other.Name}”。请换到当前物理端口后重新扫描，或先在端口设置中解除原绑定。";
        return false;
    }

    private static void EnsurePorts(AppSettings settings)
    {
        settings.Ports ??= [];
        for (var i = 0; i < settings.Ports.Count; i++)
        {
            settings.Ports[i].Order = i + 1;
            if (string.IsNullOrWhiteSpace(settings.Ports[i].Name)) settings.Ports[i].Name = $"端口 {i + 1}";
            settings.Ports[i].LocationPaths ??= [];
        }
    }

    private static AppSettings Clone(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private static Button MakeButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 100, Margin = new Thickness(0, 0, 7, 0) };
        if (primary)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            button.Foreground = Brushes.White;
            button.BorderBrush = button.Background;
        }
        button.Click += handler;
        return button;
    }
}
