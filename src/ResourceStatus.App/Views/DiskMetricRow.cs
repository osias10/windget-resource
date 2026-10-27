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

/// <summary>One selected physical disk or the PDH total; display selection never aggregates counters.</summary>
internal sealed class DiskMetricRow : Border
{
    private readonly DiskDeviceOption _device;
    private readonly HashSet<string> _elements;
    private readonly TextBlock _status;
    private readonly TextBlock? _model;
    private readonly TextBlock? _read;
    private readonly TextBlock? _write;
    private readonly HistoryChart? _chart;

    internal string DeviceId => _device.Id;

    internal DiskMetricRow(DiskDeviceOption device, MetricDisplayOptions options, bool compact, bool separator)
    {
        _device = device;
        _elements = new HashSet<string>(options.Elements, StringComparer.Ordinal);
        Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(12, 9, 12, 9);
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(0, 0, 0, separator ? 1 : 0);
        Background = Brushes.Transparent;
        var body = new StackPanel();
        Child = body;

        // A star-sized label (rather than a horizontal StackPanel) allows long names to trim.
        var heading = new Grid { MinHeight = compact ? 16 : 22 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = Text("\uEDA2", 12, Theme.Download);
        icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.Width = 12;
        icon.Margin = new Thickness(0, 0, 5, 0);
        heading.Children.Add(icon);
        var title = Text(IsTotal ? "디스크 · 전체 합계" : device.Name, compact ? 9 : 11, Theme.Text);
        title.FontWeight = FontWeights.SemiBold;
        title.ToolTip = title.Text;
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        _status = Text("", compact ? 8 : 10, Theme.Warning);
        _status.Visibility = Visibility.Collapsed;
        _status.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(_status, 2);
        heading.Children.Add(_status);
        body.Children.Add(heading);

        if (Has(ElementIds.Name))
        {
            _model = Text(IsTotal ? "모든 물리 디스크" : string.IsNullOrWhiteSpace(device.Model) ? "확인 불가" : device.Model,
                compact ? 9 : 10, Theme.Muted);
            _model.Margin = new Thickness(0, 2, 0, 0);
            _model.ToolTip = _model.Text;
            body.Children.Add(_model);
        }

        if (Has(ElementIds.Read) || Has(ElementIds.Write))
        {
            var rates = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            if (Has(ElementIds.Read))
            {
                rates.ColumnDefinitions.Add(new());
                _read = Text("읽기 —", 11, Theme.Download);
                Typography.SetNumeralAlignment(_read, FontNumeralAlignment.Tabular);
                if (Has(ElementIds.Write)) _read.Margin = new Thickness(0, 0, 6, 0);
                rates.Children.Add(_read);
            }
            if (Has(ElementIds.Write))
            {
                rates.ColumnDefinitions.Add(new());
                _write = Text("쓰기 —", 11, Theme.Upload);
                Typography.SetNumeralAlignment(_write, FontNumeralAlignment.Tabular);
                Grid.SetColumn(_write, Has(ElementIds.Read) ? 1 : 0);
                rates.Children.Add(_write);
            }
            body.Children.Add(rates);
        }
        if (Has(ElementIds.ReadGraph) || Has(ElementIds.WriteGraph))
        {
            _chart = new HistoryChart(MetricKind.Disk, compact, Has(ElementIds.ReadGraph), Has(ElementIds.WriteGraph))
            {
                Margin = new Thickness(0, compact ? 5 : 6, 0, 0)
            };
            body.Children.Add(_chart);
        }
    }

    private bool IsTotal => _device.Id.Equals(DiskCatalog.TotalId, StringComparison.OrdinalIgnoreCase);
    private bool Has(string id) => _elements.Contains(id);

    internal void Update(SystemSnapshot snapshot, IReadOnlyList<HistorySample> history)
    {
        var disk = Find(snapshot.Disks, _device.Id);
        var missing = Reading<double>.Missing(ReadingState.WarmingUp);
        var read = disk?.ReadBytesPerSecond ?? missing;
        var write = disk?.WriteBytesPerSecond ?? missing;
        SetRate(_read, "읽기", read);
        SetRate(_write, "쓰기", write);
        _chart?.Update(history, snapshot.Timestamp, _device.Id);
        ToolTip = IsTotal ? "전체 물리 디스크의 읽기·쓰기 속도" : $"{_device.Name}\n{_device.Model}".TrimEnd();

        if (disk is null && !IsTotal)
        {
            SetStatus("연결 안 됨", "선택한 디스크가 연결되어 있지 않습니다.");
            return;
        }
        var errors = new List<(string Direction, Reading<double> Reading)>();
        var hasRates = Has(ElementIds.Read) || Has(ElementIds.Write) || Has(ElementIds.ReadGraph) || Has(ElementIds.WriteGraph);
        if ((!hasRates || Has(ElementIds.Read) || Has(ElementIds.ReadGraph)) && !read.IsReady) errors.Add(("읽기", read));
        if ((!hasRates || Has(ElementIds.Write) || Has(ElementIds.WriteGraph)) && !write.IsReady) errors.Add(("쓰기", write));
        if (errors.Count == 0)
        {
            _status.Visibility = Visibility.Collapsed;
            return;
        }
        var first = errors.OrderByDescending(error => error.Reading.State).First();
        var label = Formatting.StateLabel(first.Reading.State);
        SetStatus(label, string.Join("\n", errors.Select(error =>
            $"{error.Direction}: {Formatting.StateLabel(error.Reading.State)}"
            + (string.IsNullOrWhiteSpace(error.Reading.Detail) ? "" : $" · {error.Reading.Detail}"))));
    }

    internal static DiskSnapshot? Find(IReadOnlyDictionary<string, DiskSnapshot> disks, string id)
    {
        if (disks.TryGetValue(id, out var disk)) return disk;
        return disks.FirstOrDefault(pair => pair.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private void SetStatus(string text, string detail)
    {
        _status.Visibility = Visibility.Visible;
        _status.Text = text;
        _status.ToolTip = detail;
        AutomationProperties.SetName(_status, detail);
    }

    private static void SetRate(TextBlock? target, string direction, Reading<double> value)
    {
        if (target is null) return;
        target.Text = $"{direction} {Formatting.Rate(value)}";
        target.ToolTip = target.Text;
        AutomationProperties.SetName(target, target.Text);
    }

    private static TextBlock Text(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.FontFamily,
        FontSize = size,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
