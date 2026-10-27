using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ResourceStatus.Core;

namespace ResourceStatus.App.Views;

public sealed class CompactWidgetView : WidgetView
{
    public CompactWidgetView() : base(WidgetMode.Compact) { }
}

public sealed class ExpandedWidgetView : WidgetView
{
    public ExpandedWidgetView() : base(WidgetMode.Expanded) { }
}

public abstract class WidgetView : UserControl
{
    private readonly WidgetMode _mode;
    private readonly StackPanel _content = new();
    private readonly Dictionary<MetricKind, MetricRow> _rows = new();
    private readonly List<DiskMetricRow> _diskRows = new();
    private readonly UsageColorTimeline _localColors = new();
    private string? _configuration;
    public event EventHandler? SettingsRequested;

    protected WidgetView(WidgetMode mode)
    {
        _mode = mode;
        FontFamily = Theme.FontFamily;
        Foreground = Theme.Text;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = _content;
    }

    public void Refresh(AppSettings settings, SystemSnapshot snapshot, IReadOnlyList<HistorySample> history, UsageColorTimeline? colors = null)
    {
        if (colors is null)
        {
            colors = _localColors;
            colors.Update(history, settings.UsageColors);
        }
        var profile = settings.DisplayProfiles.For(_mode);
        var disks = SelectedDisks(profile, snapshot);
        var configuration = string.Join("|", Enum.GetValues<MetricKind>().Select(kind =>
            profile.Items.TryGetValue(kind, out var item)
                ? $"{kind}:{item.Visible}:{string.Join(",", item.Elements.OrderBy(id => id, StringComparer.Ordinal))}" : $"{kind}:false"))
            + string.Join("|", disks.Select(disk => $"{disk.Id}:{disk.Name}:{disk.Model}:{disk.Number}"));
        if (_configuration != configuration)
        {
            _configuration = configuration;
            Rebuild(profile, disks);
        }
        foreach (var row in _rows.Values) row.Update(snapshot, history, colors, settings.UsageColors[row.Kind]);
        foreach (var row in _diskRows) row.Update(snapshot, history);
    }

    private static DiskDeviceOption[] SelectedDisks(DisplayProfile profile, SystemSnapshot snapshot) => profile.DiskSources
        .DistinctBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
        .Select(source => DiskMetricRow.Find(snapshot.Disks, source.Id)?.Device
            ?? (source.Id.Equals(DiskCatalog.TotalId, StringComparison.OrdinalIgnoreCase) ? DiskCatalog.TotalDevice
                : new DiskDeviceOption(source.Id, source.Name, "", null)))
        .OrderBy(device => device.Id.Equals(DiskCatalog.TotalId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(device => device.Number ?? int.MaxValue)
        .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void Rebuild(DisplayProfile profile, IReadOnlyList<DiskDeviceOption> disks)
    {
        _rows.Clear();
        _diskRows.Clear();
        _content.Children.Clear();
        var visible = Enum.GetValues<MetricKind>().Where(kind => profile.Items.TryGetValue(kind, out var options) && options.Visible).ToArray();
        foreach (var kind in visible)
        {
            if (kind == MetricKind.Disk)
            {
                for (var index = 0; index < disks.Count; index++)
                {
                    var disk = new DiskMetricRow(disks[index], profile.Items[kind], _mode == WidgetMode.Compact,
                        kind != visible[^1] || index < disks.Count - 1);
                    _diskRows.Add(disk);
                    _content.Children.Add(disk);
                }
                continue;
            }
            var row = new MetricRow(kind, profile.Items[kind], _mode == WidgetMode.Compact, kind != visible[^1]);
            _rows.Add(kind, row);
            _content.Children.Add(row);
        }
        if (visible.Length == 0)
        {
            var button = new Button
            {
                Content = "표시 항목 없음 · 설정 열기",
                FontSize = _mode == WidgetMode.Compact ? 10 : 12,
                Padding = new Thickness(8, 10, 8, 10),
                Margin = new Thickness(4),
                BorderThickness = new Thickness(0),
                ToolTip = "표시할 항목을 설정합니다."
            };
            AutomationProperties.SetName(button, "표시 항목 없음 · 설정 열기");
            button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            _content.Children.Add(button);
        }
    }

    private sealed class MetricRow : Border
    {
        private readonly MetricKind _kind;
        private readonly HashSet<string> _elements;
        private readonly bool _compact;
        private readonly StackPanel _body = new();
        private readonly TextBlock _status;
        private readonly Dictionary<string, TextBlock> _values = new();
        private readonly ThinGauge? _gauge;
        private readonly HistoryChart? _chart;
        private readonly TextBlock? _memory;
        private readonly TextBlock? _dedicated;
        private readonly TextBlock? _links;
        public MetricKind Kind => _kind;

        public MetricRow(MetricKind kind, MetricDisplayOptions options, bool compact, bool separator)
        {
            _kind = kind;
            _elements = new HashSet<string>(options.Elements, StringComparer.Ordinal);
            _compact = compact;
            Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(12, 9, 12, 9);
            BorderBrush = Theme.Border;
            BorderThickness = new Thickness(0, 0, 0, separator ? 1 : 0);
            Background = Brushes.Transparent;
            Child = _body;
            var heading = new Grid { MinHeight = compact ? 16 : 22 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = MakeText(kind switch { MetricKind.Cpu => "\uE950", MetricKind.Ram => "\uE964", MetricKind.Gpu => "\uE7F4", _ => "\uE774" }, false);
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.FontSize = 12;
            icon.Width = 12;
            icon.Foreground = Accent;
            icon.Margin = new Thickness(0, 0, 5, 0);
            identity.Children.Add(icon);
            var label = MakeText(kind switch { MetricKind.Cpu => "CPU", MetricKind.Ram => "RAM", MetricKind.Gpu => "GPU", _ => compact ? "NET" : "네트워크" }, false);
            label.FontSize = compact ? 9 : 11;
            label.FontWeight = FontWeights.SemiBold;
            identity.Children.Add(label);
            heading.Children.Add(identity);
            _status = MakeText("", false);
            _status.Margin = new Thickness(5, 0, 5, 0);
            _status.FontSize = compact ? 8 : 10;
            _status.Foreground = Theme.Warning;
            _status.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_status, 1);
            heading.Children.Add(_status);

            // Current percentage belongs to the header; hiding it creates no reserved blank column.
            if (Has(ElementIds.Usage))
            {
                var usage = MakeText("—", true);
                usage.FontSize = compact ? 11 : 20;
                usage.FontWeight = compact ? FontWeights.Medium : FontWeights.SemiBold;
                usage.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(usage, 2);
                _values.Add(ElementIds.Usage, usage);
                heading.Children.Add(usage);
            }
            else if (kind == MetricKind.Network && (Has(ElementIds.Download) || Has(ElementIds.Upload)))
            {
                var id = Has(ElementIds.Download) ? ElementIds.Download : ElementIds.Upload;
                var rate = MakeText("—", true);
                rate.Foreground = id == ElementIds.Download ? Theme.Download : Theme.Upload;
                rate.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(rate, 2);
                _values.Add(id, rate);
                heading.Children.Add(rate);
            }
            _body.Children.Add(heading);

            if (Has(ElementIds.Name)) AddLine(ElementIds.Name, "—", muted: true);
            if (kind == MetricKind.Ram && (Has(ElementIds.Used) || Has(ElementIds.Total))) _memory = AddLine("memory", "—", muted: true);
            if (kind == MetricKind.Gpu)
            {
                if (Has(ElementIds.DedicatedUsed) || Has(ElementIds.DedicatedTotal)) _dedicated = AddLine("dedicated", "—", muted: true);
                if (Has(ElementIds.SharedUsed)) AddLine(ElementIds.SharedUsed, "—", muted: true);
            }
            if (kind == MetricKind.Network)
            {
                foreach (var id in new[] { ElementIds.Download, ElementIds.Upload })
                {
                    if (!Has(id) || _values.ContainsKey(id)) continue;
                    var rate = AddLine(id, "—", muted: false);
                    rate.Foreground = id == ElementIds.Download ? Theme.Download : Theme.Upload;
                    rate.TextAlignment = compact ? TextAlignment.Right : TextAlignment.Left;
                }
                if (Has(ElementIds.ReceiveLink) || Has(ElementIds.TransmitLink)) _links = AddLine("links", "—", muted: true);
                if (Has(ElementIds.Connection)) AddLine(ElementIds.Connection, "확인 불가", muted: true);
            }
            if (Has(ElementIds.Gauge))
            {
                _gauge = new ThinGauge(Accent) { Margin = new Thickness(compact ? 45 : 0, 4, 0, 0) };
                _body.Children.Add(_gauge);
            }
            if ((kind == MetricKind.Network && (Has(ElementIds.DownloadGraph) || Has(ElementIds.UploadGraph))) ||
                (kind != MetricKind.Network && Has(ElementIds.Graph)))
            {
                _chart = new HistoryChart(kind, compact, Has(ElementIds.DownloadGraph), Has(ElementIds.UploadGraph))
                {
                    Margin = new Thickness(0, compact ? 5 : 6, 0, 0)
                };
                _body.Children.Add(_chart);
            }
        }

        private Brush Accent => _kind switch { MetricKind.Cpu => Theme.Cpu, MetricKind.Ram => Theme.Ram, MetricKind.Gpu => Theme.Gpu, _ => Theme.Download };
        private bool Has(string id) => _elements.Contains(id);

        private TextBlock MakeText(string text, bool numeric)
        {
            var result = new TextBlock
            {
                Text = text,
                FontFamily = Theme.FontFamily,
                FontSize = _compact ? 11 : 11,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (numeric) Typography.SetNumeralAlignment(result, FontNumeralAlignment.Tabular);
            return result;
        }

        private TextBlock AddLine(string id, string text, bool muted)
        {
            var block = MakeText(text, true);
            block.FontSize = _compact ? 9 : 10;
            block.Margin = new Thickness(0, 2, 0, 0);
            if (muted) block.Foreground = Theme.Muted;
            _values.Add(id, block);
            _body.Children.Add(block);
            return block;
        }

        public void Update(SystemSnapshot snapshot, IReadOnlyList<HistorySample> history, UsageColorTimeline colors, UsageColorOptions colorOptions)
        {
            var reading = _kind switch
            {
                MetricKind.Cpu => snapshot.Cpu.Usage,
                MetricKind.Ram => snapshot.Ram.Usage,
                MetricKind.Gpu => snapshot.Gpu.Usage,
                _ => snapshot.Network.LinkUsage
            };
            Set(ElementIds.Usage, Formatting.Percent(reading), _kind == MetricKind.Network ? "수신·송신 각각의 링크 속도 대비 비율 중 큰 값" : "전체 사용률");
            var level = reading.IsReady && colorOptions.Enabled ? colors.GetLevel(_kind, snapshot.Timestamp) : UsageSeverity.Normal;
            Brush Color(Brush normal) => level switch
            {
                UsageSeverity.Warning => Theme.UsageWarning,
                UsageSeverity.Critical => Theme.UsageCritical,
                _ => normal
            };
            if (_values.TryGetValue(ElementIds.Usage, out var percentage)) percentage.Foreground = Color(Theme.Text);
            if (_values.TryGetValue(ElementIds.Download, out var down))
                down.Foreground = snapshot.Network.DownloadBytesPerSecond.IsReady ? Color(Theme.Download) : Theme.Download;
            if (_values.TryGetValue(ElementIds.Upload, out var up))
                up.Foreground = snapshot.Network.UploadBytesPerSecond.IsReady ? Color(Theme.Upload) : Theme.Upload;
            _gauge?.Update(reading.IsReady ? reading.Value : null, Color(Accent));
            _chart?.Update(history, snapshot.Timestamp, _kind == MetricKind.Gpu ? snapshot.Gpu.DeviceId : snapshot.Network.DeviceId, colors, colorOptions);

            switch (_kind)
            {
                case MetricKind.Cpu:
                    Set(ElementIds.Name, DeviceName(snapshot.Cpu.Name));
                    ToolTip = snapshot.Cpu.Name;
                    break;
                case MetricKind.Ram:
                    if (_memory is not null)
                    {
                        _memory.Text = Capacity(snapshot.Ram.UsedBytes, snapshot.Ram.TotalBytes, Has(ElementIds.Used), Has(ElementIds.Total));
                        _memory.ToolTip = _memory.Text;
                    }
                    ToolTip = $"물리 메모리 · 사용 {Formatting.Bytes(snapshot.Ram.UsedBytes)} / 전체 {Formatting.Bytes(snapshot.Ram.TotalBytes)}";
                    break;
                case MetricKind.Gpu:
                    Set(ElementIds.Name, DeviceName(snapshot.Gpu.Name));
                    if (_dedicated is not null)
                    {
                        _dedicated.Text = "전용 " + Capacity(snapshot.Gpu.DedicatedUsedBytes, snapshot.Gpu.DedicatedTotalBytes, Has(ElementIds.DedicatedUsed), Has(ElementIds.DedicatedTotal));
                        _dedicated.ToolTip = _dedicated.Text;
                    }
                    Set(ElementIds.SharedUsed, $"공유 사용 {Formatting.Bytes(snapshot.Gpu.SharedUsedBytes)}");
                    ToolTip = $"{snapshot.Gpu.Name}\n전용 사용 {Formatting.Bytes(snapshot.Gpu.DedicatedUsedBytes)} / 전체 {Formatting.Bytes(snapshot.Gpu.DedicatedTotalBytes)}\n공유 사용 {Formatting.Bytes(snapshot.Gpu.SharedUsedBytes)}\n가장 바쁜 GPU 엔진의 사용률";
                    break;
                case MetricKind.Network:
                    var network = snapshot.Network;
                    Set(ElementIds.Name, DeviceName(network.Name));
                    Set(ElementIds.Download, "↓ " + Formatting.Rate(network.DownloadBytesPerSecond), "다운로드 속도");
                    Set(ElementIds.Upload, "↑ " + Formatting.Rate(network.UploadBytesPerSecond), "업로드 속도");
                    if (_links is not null)
                    {
                        _links.Text = "링크 " + string.Join(" · ", new[] {
                            Has(ElementIds.ReceiveLink) ? $"↓ {Formatting.LinkSpeed(network.ReceiveLinkBitsPerSecond)}" : null,
                            Has(ElementIds.TransmitLink) ? $"↑ {Formatting.LinkSpeed(network.TransmitLinkBitsPerSecond)}" : null }.Where(x => x is not null));
                        _links.ToolTip = _links.Text;
                    }
                    Set(ElementIds.Connection, "● " + network.ConnectionStatus);
                    if (_values.TryGetValue(ElementIds.Connection, out var status))
                        status.Foreground = network.ConnectionStatus == "인터넷 연결" ? Theme.Gpu : Theme.Muted;
                    ToolTip = $"{network.Name}\n수신 링크 {Formatting.LinkSpeed(network.ReceiveLinkBitsPerSecond)} · 송신 링크 {Formatting.LinkSpeed(network.TransmitLinkBitsPerSecond)}\n{network.ConnectionStatus}";
                    break;
            }
            UpdateStatus(snapshot);
        }

        private void UpdateStatus(SystemSnapshot snapshot)
        {
            var errors = new List<(ReadingState State, string? Detail)>();
            void Track<T>(Reading<T> reading, bool used) where T : struct
            {
                if (used && !reading.IsReady) errors.Add((reading.State, reading.Detail));
            }
            var usage = Has(ElementIds.Usage) || Has(ElementIds.Gauge) || Has(ElementIds.Graph);
            switch (_kind)
            {
                case MetricKind.Cpu:
                    Track(snapshot.Cpu.Usage, usage);
                    break;
                case MetricKind.Ram:
                    Track(snapshot.Ram.Usage, usage);
                    Track(snapshot.Ram.UsedBytes, Has(ElementIds.Used));
                    Track(snapshot.Ram.TotalBytes, Has(ElementIds.Total));
                    break;
                case MetricKind.Gpu:
                    Track(snapshot.Gpu.Usage, usage);
                    Track(snapshot.Gpu.DedicatedUsedBytes, Has(ElementIds.DedicatedUsed));
                    Track(snapshot.Gpu.DedicatedTotalBytes, Has(ElementIds.DedicatedTotal));
                    Track(snapshot.Gpu.SharedUsedBytes, Has(ElementIds.SharedUsed));
                    break;
                case MetricKind.Network:
                    Track(snapshot.Network.LinkUsage, usage);
                    Track(snapshot.Network.DownloadBytesPerSecond, Has(ElementIds.Download) || Has(ElementIds.DownloadGraph));
                    Track(snapshot.Network.UploadBytesPerSecond, Has(ElementIds.Upload) || Has(ElementIds.UploadGraph));
                    Track(snapshot.Network.ReceiveLinkBitsPerSecond, Has(ElementIds.ReceiveLink));
                    Track(snapshot.Network.TransmitLinkBitsPerSecond, Has(ElementIds.TransmitLink));
                    break;
            }
            _status.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (errors.Count == 0) return;
            var first = errors.OrderByDescending(error => error.State).First();
            var label = Formatting.StateLabel(first.State);
            _status.Text = _compact ? (first.State == ReadingState.WarmingUp ? "◷" : "!") : label;
            _status.ToolTip = string.Join("\n", errors.Select(error => string.IsNullOrWhiteSpace(error.Detail)
                ? Formatting.StateLabel(error.State) : $"{Formatting.StateLabel(error.State)} · {error.Detail}").Distinct());
            AutomationProperties.SetName(_status, label);
        }

        private void Set(string id, string value, string? detail = null)
        {
            if (!_values.TryGetValue(id, out var block)) return;
            block.Text = value;
            block.ToolTip = detail is null ? value : $"{detail} · {value}";
            AutomationProperties.SetName(block, detail is null ? value : $"{detail} {value}");
        }

        private static string DeviceName(string value) => string.IsNullOrWhiteSpace(value) ? "확인 불가" : value;

        private static string Capacity(Reading<ulong> used, Reading<ulong> total, bool showUsed, bool showTotal)
        {
            if (showUsed && showTotal) return $"{Formatting.Bytes(used)} / {Formatting.Bytes(total)}";
            return showUsed ? $"사용 {Formatting.Bytes(used)}" : $"전체 {Formatting.Bytes(total)}";
        }
    }
}
