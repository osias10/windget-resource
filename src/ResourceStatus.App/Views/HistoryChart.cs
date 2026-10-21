using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ResourceStatus.Core;

namespace ResourceStatus.App.Views;

/// <summary>A lightweight, timestamp-based chart. Missing samples are deliberately left blank.</summary>
public sealed class HistoryChart : FrameworkElement
{
    private IReadOnlyList<HistorySample> _history = Array.Empty<HistorySample>();
    private DateTimeOffset _now;
    private string? _deviceId;
    private readonly MetricKind _kind;
    private readonly bool _compact;
    private readonly bool _download;
    private readonly bool _upload;
    private static readonly Typeface Typeface = new(Theme.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    // Per-instance render cache (static labels, pens, clip), rebuilt when the theme or DPI changes.
    private readonly Dictionary<string, FormattedText> _labels = [];
    private FormattedText? _axis;
    private Pen? _gridPen, _seriesPen, _uploadPen;
    private RectangleGeometry? _clip;
    private bool _cacheDark;
    private double _cacheDip;

    public HistoryChart(MetricKind kind, bool compact, bool download = false, bool upload = false)
    {
        _kind = kind;
        _compact = compact;
        _download = download;
        _upload = upload;
        Height = compact ? 28 : 56;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public void Update(IReadOnlyList<HistorySample> history, DateTimeOffset now, string? deviceId)
    {
        _history = history;
        _now = now;
        _deviceId = deviceId;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 24 || ActualHeight < 20) return;
        var width = ActualWidth;
        var top = _compact ? 9d : 12d;
        var bottom = ActualHeight - (_compact ? 8d : 10d);
        var chart = new Rect(0, top, width, Math.Max(2, bottom - top));
        var max = _kind == MetricKind.Network ? NetworkMaximum() : 100d;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_gridPen is null || _cacheDark != Theme.IsDark || _cacheDip != dip)
        {
            _labels.Clear();
            _axis = null;
            _seriesPen = _uploadPen = null;
            _gridPen = new Pen(Theme.Track, .6);
            _cacheDark = Theme.IsDark;
            _cacheDip = dip;
        }
        var pen = _gridPen;
        dc.DrawLine(pen, new Point(0, chart.Top), new Point(width, chart.Top));
        dc.DrawLine(pen, new Point(0, chart.Bottom), new Point(width, chart.Bottom));
        if (!_compact) dc.DrawLine(pen, new Point(0, chart.Top + chart.Height / 2), new Point(width, chart.Top + chart.Height / 2));

        if (_kind == MetricKind.Network)
        {
            double legendX = 0;
            if (_download) legendX += Label(dc, Static("↓ 수신", Theme.Download, dip), legendX, 0) + 8;
            if (_upload) Label(dc, Static("↑ 송신", Theme.Upload, dip), legendX, 0);
            var axis = FormatRate(max);
            if (_axis?.Text != axis) _axis = Format(axis, Theme.Muted, dip);
            Label(dc, _axis, width, 0, true);
        }
        else Label(dc, Static("100%", Theme.Muted, dip), 0, 0);
        Label(dc, Static("−60초", Theme.Muted, dip), 0, bottom);
        Label(dc, Static("현재", Theme.Muted, dip), width, bottom, true);

        if (_clip?.Rect != chart) { _clip = new RectangleGeometry(chart); _clip.Freeze(); }
        dc.PushClip(_clip);
        if (_kind == MetricKind.Network)
        {
            if (_download) DrawSeries(dc, chart, max, sample => sample.NetworkId == _deviceId ? sample.Download : null, _seriesPen ??= SeriesPen(Theme.Download));
            if (_upload) DrawSeries(dc, chart, max, sample => sample.NetworkId == _deviceId ? sample.Upload : null, _uploadPen ??= SeriesPen(Theme.Upload));
        }
        else
        {
            Func<HistorySample, double?> select = _kind switch
            {
                MetricKind.Cpu => sample => sample.Cpu,
                MetricKind.Ram => sample => sample.Ram,
                _ => sample => sample.GpuId == _deviceId ? sample.Gpu : null
            };
            DrawSeries(dc, chart, max, select, _seriesPen ??= SeriesPen(_kind == MetricKind.Cpu ? Theme.Cpu : _kind == MetricKind.Ram ? Theme.Ram : Theme.Gpu));
        }
        dc.Pop();
    }

    private double NetworkMaximum()
    {
        var maximum = 0d;
        foreach (var sample in _history)
        {
            if (sample.NetworkId != _deviceId || (_now - sample.Timestamp).TotalSeconds is < 0 or > 60) continue;
            if (_download && sample.Download is double down && double.IsFinite(down)) maximum = Math.Max(maximum, down);
            if (_upload && sample.Upload is double up && double.IsFinite(up)) maximum = Math.Max(maximum, up);
        }
        // A stable, legible ceiling in decimal byte units; idle charts still have a meaningful axis.
        maximum = Math.Max(1000, maximum * 1.1);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        return Math.Ceiling(maximum / magnitude / .5) * magnitude * .5;
    }

    private Pen SeriesPen(Brush brush) => new(brush, _compact ? 1 : 1.35) { LineJoin = PenLineJoin.Round };

    private void DrawSeries(DrawingContext dc, Rect area, double max, Func<HistorySample, double?> select, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            DateTimeOffset? previous = null;
            foreach (var sample in _history)
            {
                var age = (_now - sample.Timestamp).TotalSeconds;
                var value = select(sample);
                if (age is < 0 or > 60 || value is not double number || !double.IsFinite(number))
                {
                    previous = null;
                    continue;
                }
                var point = new Point(area.Left + (1 - age / 60d) * area.Width,
                    area.Bottom - Math.Clamp(number / max, 0, 1) * area.Height);
                if (previous is null || (sample.Timestamp - previous.Value).TotalSeconds > 2.5 || sample.Timestamp <= previous.Value)
                    context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
                previous = sample.Timestamp;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private FormattedText Static(string text, Brush color, double dip)
    {
        if (!_labels.TryGetValue(text, out var formatted)) _labels[text] = formatted = Format(text, color, dip);
        return formatted;
    }

    private FormattedText Format(string text, Brush color, double dip) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        Typeface, _compact ? 7 : 8, color, dip);

    private static double Label(DrawingContext dc, FormattedText formatted, double x, double y, bool alignRight = false)
    {
        dc.DrawText(formatted, new Point(alignRight ? Math.Max(0, x - formatted.Width) : x, y));
        return formatted.Width;
    }

    private static string FormatRate(double value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000:0.#} GB/s"
        : value >= 1_000_000 ? $"{value / 1_000_000:0.#} MB/s" : value >= 1000 ? $"{value / 1000:0.#} KB/s" : $"{value:0} B/s";
}

internal sealed class ThinGauge : FrameworkElement
{
    private double? _value;
    private readonly Brush _brush;

    public ThinGauge(Brush brush)
    {
        _brush = brush;
        Height = 2;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Update(double? value)
    {
        if (_value == value) return;
        _value = value;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, ActualWidth, ActualHeight), 1, 1);
        if (_value is double value && double.IsFinite(value))
            dc.DrawRoundedRectangle(_brush, null, new Rect(0, 0, ActualWidth * Math.Clamp(value / 100, 0, 1), ActualHeight), 1, 1);
    }
}
