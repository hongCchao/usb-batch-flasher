using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UsbBatchTool.Views;

/// <summary>
/// 端口绑定与写入设置窗口。窗口只修改配置副本，点击“保存”后才返回结果。
/// 端口匹配使用 LocationPath；LastSerial 只作为信息展示，不参与端口匹配。
/// </summary>
public sealed class PortSettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly IReadOnlyList<UsbDisk> _disks;
    private readonly ListBox _portsList = new();
    private readonly ListBox _disksList = new();
    private readonly ListBox _aliasesList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _portCountBox = new();
    private readonly TextBox _concurrentBox = new();
    private readonly TextBox _refreshBox = new();
    private readonly CheckBox _verifyBox = new();
    private readonly TextBlock _selectionInfo = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _aliasHint = new();
    private bool _updatingSelection;
    private int _selectedPortIndex = -1;

    public AppSettings ResultSettings { get; private set; }

    public PortSettingsWindow(AppSettings settings, IReadOnlyList<UsbDisk> disks)
    {
        _working = Clone(settings);
        _disks = disks ?? Array.Empty<UsbDisk>();
        EnsurePorts(_working);
        ResultSettings = Clone(_working);

        Title = "端口与设备绑定设置";
        Width = 980;
        Height = 650;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        BuildUi();
        RefreshPortList();
        _disksList.ItemsSource = _disks;
        _disksList.DisplayMemberPath = nameof(UsbDisk.DisplayName);
        UpdateDeviceSummary();
        _concurrentBox.Text = _working.ConcurrentJobs.ToString();
        _refreshBox.Text = _working.RefreshSeconds.ToString();
        _verifyBox.IsChecked = _working.VerifyAfterWrite;
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = "USB 端口绑定",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 43, 77))
        });
        heading.Children.Add(new TextBlock
        {
            Text = "端口使用 USB Location Path 匹配。更换 U 盘后，只要仍接入同一物理端口即可自动匹配；序列号仅用于显示。",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139)),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var portPanel = new Grid();
        portPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        portPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        portPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        portPanel.Children.Add(new TextBlock { Text = "端口列表", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        _portsList.Margin = new Thickness(0, 0, 12, 8);
        _portsList.BorderBrush = new SolidColorBrush(Color.FromRgb(214, 223, 236));
        _portsList.SelectionChanged += PortsList_SelectionChanged;
        Grid.SetRow(_portsList, 1);
        portPanel.Children.Add(_portsList);

        var portButtons = new WrapPanel();
        portButtons.Children.Add(MakeButton("新增端口", AddPort));
        portButtons.Children.Add(MakeButton("删除端口", DeleteSelectedPort));
        portButtons.Children.Add(MakeButton("↑ 上移", (_, _) => MovePort(-1)));
        portButtons.Children.Add(MakeButton("↓ 下移", (_, _) => MovePort(1)));
        Grid.SetRow(portButtons, 2);
        portPanel.Children.Add(portButtons);
        Grid.SetColumn(portPanel, 0);
        body.Children.Add(portPanel);

        var editor = new Grid { Margin = new Thickness(0, 0, 16, 0) };
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        editor.Children.Add(new TextBlock { Text = "端口名称", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        _nameBox.Margin = new Thickness(0, 0, 0, 12);
        _nameBox.TextChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            var port = SelectedPort;
            if (port is null) return;
            port.Name = _nameBox.Text.Trim();
            if (_portsList.SelectedItem is ListBoxItem item && item.Content is StackPanel stack && stack.Children[0] is TextBlock label)
                label.Text = $"{port.Order}. {port.Name}";
        };
        Grid.SetRow(_nameBox, 1);
        editor.Children.Add(_nameBox);

        var countLine = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        countLine.Children.Add(new TextBlock { Text = "端口数量", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        _portCountBox.Width = 70;
        _portCountBox.Text = _working.Ports.Count.ToString();
        countLine.Children.Add(_portCountBox);
        var applyCount = MakeButton("应用", (_, _) => TryApplyPortCount());
        applyCount.Margin = new Thickness(7, 0, 0, 0);
        countLine.Children.Add(applyCount);
        countLine.Children.Add(new TextBlock { Text = "范围 0～32", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139)), Margin = new Thickness(9, 0, 0, 0) });
        Grid.SetRow(countLine, 2);
        editor.Children.Add(countLine);

        var bindingButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        bindingButtons.Children.Add(MakeButton("绑定当前设备", BindCurrentDevice));
        bindingButtons.Children.Add(MakeButton("追加位置别名", AppendLocationAlias));
        var clearBinding = MakeButton("解除绑定", ClearSelectedBinding);
        clearBinding.Margin = new Thickness(0, 5, 7, 0);
        bindingButtons.Children.Add(clearBinding);
        Grid.SetRow(bindingButtons, 3);
        editor.Children.Add(bindingButtons);

        var locationPanel = new Grid();
        locationPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        locationPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        locationPanel.Children.Add(new TextBlock { Text = "当前端口 Location Path 别名", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        _aliasesList.BorderBrush = new SolidColorBrush(Color.FromRgb(214, 223, 236));
        _aliasesList.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetRow(_aliasesList, 1);
        locationPanel.Children.Add(_aliasesList);
        Grid.SetRow(locationPanel, 4);
        editor.Children.Add(locationPanel);

        _aliasHint.Text = "USB 2.0/3.0 路径可以作为同一物理端口的别名追加。";
        _aliasHint.Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139));
        _aliasHint.TextWrapping = TextWrapping.Wrap;
        _aliasHint.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_aliasHint, 5);
        editor.Children.Add(_aliasHint);
        Grid.SetColumn(editor, 1);
        body.Children.Add(editor);

        var devicePanel = new Grid();
        devicePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        devicePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        devicePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        devicePanel.Children.Add(new TextBlock { Text = "当前检测到的 USB 磁盘", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        _disksList.BorderBrush = new SolidColorBrush(Color.FromRgb(214, 223, 236));
        _disksList.SelectionChanged += (_, _) => UpdateDeviceSummary();
        Grid.SetRow(_disksList, 1);
        devicePanel.Children.Add(_disksList);
        _selectionInfo.Margin = new Thickness(0, 9, 0, 0);
        _selectionInfo.Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139));
        _selectionInfo.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_selectionInfo, 2);
        devicePanel.Children.Add(_selectionInfo);
        Grid.SetColumn(devicePanel, 2);
        body.Children.Add(devicePanel);

        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        options.Children.Add(new TextBlock { Text = "同时写入设备数", VerticalAlignment = VerticalAlignment.Center });
        _concurrentBox.Width = 50; _concurrentBox.Margin = new Thickness(8, 0, 18, 0); options.Children.Add(_concurrentBox);
        options.Children.Add(new TextBlock { Text = "自动刷新间隔（秒）", VerticalAlignment = VerticalAlignment.Center });
        _refreshBox.Width = 55; _refreshBox.Margin = new Thickness(8, 0, 18, 0); options.Children.Add(_refreshBox);
        _verifyBox.Content = "镜像写入后读回校验"; _verifyBox.VerticalAlignment = VerticalAlignment.Center; options.Children.Add(_verifyBox);
        Grid.SetRow(options, 2); root.Children.Add(options);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leftFooter = new StackPanel { Orientation = Orientation.Horizontal };
        var reset = MakeButton("清空所有端口", ResetDefaults);
        leftFooter.Children.Add(reset);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139));
        _statusText.Margin = new Thickness(10, 0, 0, 0);
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.MaxWidth = 390;
        leftFooter.Children.Add(_statusText);
        footer.Children.Add(leftFooter);
        var rightFooter = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = MakeButton("取消", (_, _) => { DialogResult = false; Close(); });
        var save = MakeButton("保存设置", SaveSettings, true);
        rightFooter.Children.Add(save);
        rightFooter.Children.Add(cancel);
        Grid.SetColumn(rightFooter, 1);
        footer.Children.Add(rightFooter);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
    }

    private PortBinding? SelectedPort => _selectedPortIndex >= 0 && _selectedPortIndex < _working.Ports.Count ? _working.Ports[_selectedPortIndex] : null;

    private void RefreshPortList(int? selectIndex = null)
    {
        NormalizeOrders(_working);
        _portsList.Items.Clear();
        foreach (var port in _working.Ports)
        {
            var paths = port.LocationPaths.Count == 0 ? port.FallbackDeviceKey.Length > 0 ? "设备身份绑定" : "未绑定设备" : $"已绑定 {port.LocationPaths.Count} 个位置别名";
            var stack = new StackPanel { Margin = new Thickness(7, 6, 7, 6) };
            stack.Children.Add(new TextBlock { Text = $"{port.Order}. {port.Name}", FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = paths, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 139)) });
            _portsList.Items.Add(new ListBoxItem { Content = stack, Tag = port });
        }
        var target = selectIndex ?? Math.Clamp(_selectedPortIndex, 0, Math.Max(0, _working.Ports.Count - 1));
        if (_working.Ports.Count > 0)
        {
            _portsList.SelectedIndex = target;
        }
        else
        {
            _selectedPortIndex = -1;
            UpdateSelectedPortEditor();
        }
    }

    private void PortsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPortIndex = _portsList.SelectedIndex;
        UpdateSelectedPortEditor();
    }

    private void UpdateSelectedPortEditor()
    {
        var port = SelectedPort;
        _updatingSelection = true;
        _nameBox.Text = port?.Name ?? "";
        _updatingSelection = false;
        _aliasesList.ItemsSource = port?.LocationPaths.ToList() ?? new List<string>();
        _statusText.Text = port is null ? "当前没有端口。插入 U 盘后可在主界面添加，或使用上方“新增端口”。" : $"当前端口：{port.Order} · {port.Name}";
    }

    private void UpdateDeviceSummary()
    {
        if (_disks.Count == 0)
        {
            _selectionInfo.Text = "当前没有检测到可用 USB 磁盘。";
            return;
        }
        if (_disksList.SelectedItem is not UsbDisk disk)
        {
            _selectionInfo.Text = $"检测到 {_disks.Count} 个设备，请选择一个用于绑定。";
            return;
        }
        var paths = disk.LocationPaths.Count == 0 ? "无 Location Path" : string.Join("\n", disk.LocationPaths);
        _selectionInfo.Text = $"设备：{disk.Model}\n物理磁盘：{disk.DevicePath}\n容量：{disk.CapacityText}\n序列号：{(string.IsNullOrWhiteSpace(disk.SerialNumber) ? "无" : disk.SerialNumber)}\n可写入：{(disk.CanWrite ? "是" : "否" + (string.IsNullOrWhiteSpace(disk.SafetyReason) ? "" : "（" + disk.SafetyReason + "）"))}\n位置：{paths}";
    }

    private void BindCurrentDevice(object? sender, RoutedEventArgs e)
    {
        var port = SelectedPort;
        var disk = _disksList.SelectedItem as UsbDisk;
        if (port is null || disk is null)
        {
            _statusText.Text = "请先选择端口和 USB 磁盘。";
            return;
        }
        if (!ValidateBindingTarget(port, disk)) return;
        port.LocationPaths = disk.LocationPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        port.FallbackDeviceKey = disk.LocationPaths.Count == 0 ? disk.PersistentDeviceKey : "";
        port.LastModel = disk.Model;
        port.LastSerial = disk.SerialNumber;
        RefreshPortList(_selectedPortIndex);
        _statusText.Text = disk.LocationPaths.Count == 0 ? "已按设备身份绑定；更换 U 盘后必须重新手动绑定。" : $"已将 {disk.DisplayName} 绑定到端口 {port.Order}。";
    }

    private void AppendLocationAlias(object? sender, RoutedEventArgs e)
    {
        var port = SelectedPort;
        var disk = _disksList.SelectedItem as UsbDisk;
        if (port is null || disk is null)
        {
            _statusText.Text = "请先选择端口和 USB 磁盘。";
            return;
        }
        if (disk.LocationPaths.Count == 0)
        {
            _statusText.Text = "所选设备没有可追加的 Location Path。";
            return;
        }
        if (!ValidateBindingTarget(port, disk)) return;
        if (MessageBox.Show(this,
                $"请确认 {disk.DisplayName} 连接在“{port.Name}”的同一个物理插口，再追加 USB 2.0/3.0 位置路径别名。是否继续？",
                "确认位置别名", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        port.LocationPaths ??= [];
        port.FallbackDeviceKey = "";
        foreach (var path in disk.LocationPaths.Where(p => !port.LocationPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            port.LocationPaths.Add(path);
        port.LastModel = disk.Model;
        port.LastSerial = disk.SerialNumber;
        UpdateSelectedPortEditor();
        RefreshPortList(_selectedPortIndex);
        _statusText.Text = $"已追加 {disk.LocationPaths.Count} 个位置别名。";
    }

    private void ClearSelectedBinding(object? sender, RoutedEventArgs e)
    {
        var port = SelectedPort;
        if (port is null) return;
        if ((port.LocationPaths.Count > 0 || port.FallbackDeviceKey.Length > 0) && MessageBox.Show(this, $"解除“{port.Name}”的绑定？", "解除绑定", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        port.LocationPaths.Clear();
        port.FallbackDeviceKey = "";
        port.LastModel = "";
        port.LastSerial = "";
        RefreshPortList(_selectedPortIndex);
        _statusText.Text = "端口绑定已解除，保存后生效。";
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
        _statusText.Text = $"该位置路径已绑定到“{other.Name}”，请先解除原绑定。";
        return false;
    }

    private void MovePort(int direction)
    {
        if (_selectedPortIndex < 0 || _selectedPortIndex >= _working.Ports.Count) return;
        var target = _selectedPortIndex + direction;
        if (target < 0 || target >= _working.Ports.Count) return;
        (_working.Ports[_selectedPortIndex], _working.Ports[target]) = (_working.Ports[target], _working.Ports[_selectedPortIndex]);
        NormalizeOrders(_working);
        _selectedPortIndex = target;
        RefreshPortList(_selectedPortIndex);
    }

    private void AddPort(object? sender, RoutedEventArgs e)
    {
        if (_working.Ports.Count >= 32)
        {
            _statusText.Text = "最多可配置 32 个端口。";
            return;
        }
        var order = _working.Ports.Count + 1;
        _working.Ports.Add(new PortBinding { Order = order, Name = $"端口 {order}" });
        _portCountBox.Text = _working.Ports.Count.ToString();
        _selectedPortIndex = order - 1;
        RefreshPortList(_selectedPortIndex);
        _statusText.Text = $"已新增端口 {order}，请选择设备进行绑定；保存后生效。";
    }

    private void DeleteSelectedPort(object? sender, RoutedEventArgs e)
    {
        var port = SelectedPort;
        if (port is null) return;
        if (MessageBox.Show(this, $"删除“{port.Name}”及其设备绑定？保存后生效。", "删除端口", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        RemoveSelectedPort();
    }

    private void RemoveSelectedPort()
    {
        if (SelectedPort is null) return;
        _working.Ports.RemoveAt(_selectedPortIndex);
        _portCountBox.Text = _working.Ports.Count.ToString();
        _selectedPortIndex = Math.Min(_selectedPortIndex, _working.Ports.Count - 1);
        RefreshPortList(_selectedPortIndex);
        _statusText.Text = "端口已删除，保存后生效。";
    }

    private void ResetDefaults(object? sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将清除全部端口、名称和绑定，恢复为 0 个端口。保存后生效，是否继续？", "清空所有端口", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _working.Ports.Clear();
        _portCountBox.Text = "0";
        _selectedPortIndex = -1;
        RefreshPortList();
        _statusText.Text = "已清空全部端口，保存后生效。";
    }

    private void SaveSettings(object? sender, RoutedEventArgs e)
    {
        if (!TryApplyPortCount()) return;
        if (!int.TryParse(_concurrentBox.Text.Trim(), out int concurrent) || concurrent < 1 || concurrent > 7 ||
            !int.TryParse(_refreshBox.Text.Trim(), out int refresh) || refresh < 2 || refresh > 60)
        {
            MessageBox.Show(this, "并发数应为 1～7，自动刷新间隔应为 2～60 秒。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _working.ConcurrentJobs = concurrent;
        _working.RefreshSeconds = refresh;
        _working.VerifyAfterWrite = _verifyBox.IsChecked == true;
        NormalizeOrders(_working);
        ResultSettings = Clone(_working);
        DialogResult = true;
        Close();
    }

    private bool TryApplyPortCount()
    {
        if (!int.TryParse(_portCountBox.Text.Trim(), out var count) || count < 0 || count > 32)
        {
            MessageBox.Show(this, "端口数量必须是 0 到 32 之间的整数。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (count < _working.Ports.Count && MessageBox.Show(this,
                $"将移除第 {count + 1} 至第 {_working.Ports.Count} 个端口及其绑定。是否继续？",
                "减少端口数量", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        ApplyPortCount(count);
        _selectedPortIndex = _working.Ports.Count == 0 ? -1 : Math.Clamp(_selectedPortIndex, 0, _working.Ports.Count - 1);
        RefreshPortList(_selectedPortIndex);
        return true;
    }

    private void ApplyPortCount(int count)
    {
        if (_working.Ports.Count > count)
            _working.Ports = _working.Ports.Take(count).ToList();
        while (_working.Ports.Count < count)
        {
            var order = _working.Ports.Count + 1;
            _working.Ports.Add(new PortBinding { Order = order, Name = $"端口 {order}" });
        }
    }

    private static void EnsurePorts(AppSettings settings)
    {
        settings.Ports ??= [];
        NormalizeOrders(settings);
    }

    private static void NormalizeOrders(AppSettings settings)
    {
        settings.Ports ??= [];
        for (var i = 0; i < settings.Ports.Count; i++)
        {
            var oldOrder = settings.Ports[i].Order;
            if (settings.Ports[i].Name == $"端口 {oldOrder}") settings.Ports[i].Name = $"端口 {i + 1}";
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
        var button = new Button { Content = text, MinWidth = 84, Margin = new Thickness(0, 0, 7, 0) };
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
