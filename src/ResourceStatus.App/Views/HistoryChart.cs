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
    private UsageColorTimeline? _colors;
    private UsageColorOptions? _options;
    private readonly MetricKind _kind;
    private readonly bool _compact;
    private readonly bool _download;
    private readonly bool _upload;
    private static readonly Typeface Typeface = new(Theme.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    // Per-instance render cache (static labels, pens, clip), rebuilt when the theme or DPI changes.
    private readonly Dictionary<(string Text, Brush Color), FormattedText> _labels = [];
    private FormattedText? _axis;
    private Pen? _gridPen, _seriesPen, _uploadPen;
    private Pen? _warningPen, _criticalPen, _uploadWarningPen, _uploadCriticalPen;
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

    public void Update(IReadOnlyList<HistorySample> history, DateTimeOffset now, string? deviceId,
        UsageColorTimeline? colors = null, UsageColorOptions? options = null)
    {
        _history = history;
        _now = now;
        _deviceId = deviceId;
        _colors = colors;
        _options = options;
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
            _warningPen = _criticalPen = _uploadWarningPen = _uploadCriticalPen = null;
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
            var level = _options?.Enabled == true ? _colors?.GetLevel(_kind, _now) ?? UsageSeverity.Normal : UsageSeverity.Normal;
            if (_download) legendX += Legend(dc, "↓ 수신", level, false, legendX, dip) + 8;
            if (_upload) Legend(dc, "↑ 송신", level, true, legendX, dip);
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
            if (_download) DrawSeries(dc, chart, max, sample => sample.NetworkId == _deviceId ? sample.Download : null, false);
            if (_upload) DrawSeries(dc, chart, max, sample => sample.NetworkId == _deviceId ? sample.Upload : null, true);
        }
        else
        {
            Func<HistorySample, double?> select = _kind switch
            {
                MetricKind.Cpu => sample => sample.Cpu,
                MetricKind.Ram => sample => sample.Ram,
                _ => sample => sample.GpuId == _deviceId ? sample.Gpu : null
            };
            DrawSeries(dc, chart, max, select, false);
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

    private Brush NormalBrush => _kind switch
    {
        MetricKind.Cpu => Theme.Cpu, MetricKind.Ram => Theme.Ram, MetricKind.Gpu => Theme.Gpu, _ => Theme.Download
    };

    private static Brush Color(UsageSeverity level, Brush normal) => level switch
    {
        UsageSeverity.Warning => Theme.UsageWarning, UsageSeverity.Critical => Theme.UsageCritical, _ => normal
    };

    private Pen SeriesPen(Brush brush, bool upload = false) => new(brush, _compact ? 1 : 1.35)
    {
        LineJoin = PenLineJoin.Round, DashStyle = upload ? DashStyles.Dash : DashStyles.Solid
    };

    private Pen PenFor(UsageSeverity level, bool upload) => (level, upload) switch
    {
        (UsageSeverity.Warning, false) => _warningPen ??= SeriesPen(Theme.UsageWarning),
        (UsageSeverity.Critical, false) => _criticalPen ??= SeriesPen(Theme.UsageCritical),
        (UsageSeverity.Warning, true) => _uploadWarningPen ??= SeriesPen(Theme.UsageWarning, true),
        (UsageSeverity.Critical, true) => _uploadCriticalPen ??= SeriesPen(Theme.UsageCritical, true),
        (_, true) => _uploadPen ??= SeriesPen(Theme.Upload, true),
        _ => _seriesPen ??= SeriesPen(NormalBrush)
    };

    private double Legend(DrawingContext dc, string text, UsageSeverity level, bool upload, double x, double dip)
    {
        dc.DrawLine(PenFor(level, upload), new Point(x, 5), new Point(x + 9, 5));
        return 12 + Label(dc, Static(text, Color(level, upload ? Theme.Upload : Theme.Download), dip), x + 12, 0);
    }

    private void DrawSeries(DrawingContext dc, Rect area, double max, Func<HistorySample, double?> select, bool upload)
    {
        var paths = new Dictionary<UsageSeverity, (StreamGeometry Geometry, StreamGeometryContext Context, Point End)>();
        HistorySample? previous = null;
        Point previousPoint = default;
        try
        {
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
                if (previous is not null && (sample.Timestamp - previous.Timestamp).TotalSeconds <= 2.5 && sample.Timestamp > previous.Timestamp
                    && (_kind != MetricKind.Gpu || previous.GpuId == sample.GpuId)
                    && (_kind != MetricKind.Network || previous.NetworkId == sample.NetworkId))
                    DrawInterval(previous, sample, previousPoint, point);
                previous = sample;
                previousPoint = point;
            }
        }
        finally
        {
            foreach (var path in paths.Values) ((IDisposable)path.Context).Dispose();
        }
        foreach (var (level, path) in paths)
        {
            path.Geometry.Freeze();
            dc.DrawGeometry(null, PenFor(level, upload), path.Geometry);
        }

        void Line(Point from, Point to, UsageSeverity level)
        {
            if (!paths.TryGetValue(level, out var path))
            {
                var geometry = new StreamGeometry();
                path = (geometry, geometry.Open(), new Point(double.NaN, double.NaN));
            }
            if (path.End != from) path.Context.BeginFigure(from, false, false);
            path.Context.LineTo(to, true, false);
            paths[level] = (path.Geometry, path.Context, to);
        }

        void DrawInterval(HistorySample start, HistorySample end, Point from, Point to)
        {
            var startUsage = Usage(start);
            var endUsage = Usage(end);
            if (_options?.Enabled != true || _colors is null
                || startUsage is not double a || endUsage is not double b
                || !double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < 0)
            {
                Line(from, to, UsageSeverity.Normal);
                return;
            }
            a = Math.Min(100, a);
            b = Math.Min(100, b);
            // Split at entry and recovery thresholds, so only the affected section changes color.
            // Network curves keep their byte/s axis; their color follows the recorded link percentage.
            Span<double> cuts = stackalloc double[6];
            cuts[0] = 0;
            var count = 1;
            if (a != b)
            {
                foreach (var boundary in new[] { (double)_options.WarningThreshold, _options.CriticalThreshold,
                    UsageColorPolicy.RecoveryThreshold(_options.WarningThreshold), UsageColorPolicy.RecoveryThreshold(_options.CriticalThreshold) })
                {
                    var fraction = (boundary - a) / (b - a);
                    if (fraction > 0 && fraction < 1) cuts[count++] = fraction;
                }
            }
            cuts[count++] = 1;
            cuts[..count].Sort();
            var state = _colors.GetLevel(_kind, start.Timestamp);
            for (var index = 1; index < count; index++)
            {
                var left = cuts[index - 1];
                var right = cuts[index];
                if (right <= left) continue;
                state = UsageColorPolicy.Evaluate(a + (b - a) * ((left + right) / 2), _options, state);
                Line(from + (to - from) * left, from + (to - from) * right, state);
            }
        }
    }

    private double? Usage(HistorySample sample) => _kind switch
    {
        MetricKind.Cpu => sample.Cpu, MetricKind.Ram => sample.Ram, MetricKind.Gpu => sample.Gpu, _ => sample.LinkUsage
    };

    private FormattedText Static(string text, Brush color, double dip)
    {
        if (!_labels.TryGetValue((text, color), out var formatted)) _labels[(text, color)] = formatted = Format(text, color, dip);
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
    private Brush _brush;
    internal Brush FillBrush => _brush;

    public ThinGauge(Brush brush)
    {
        _brush = brush;
        Height = 2;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Update(double? value, Brush? brush = null)
    {
        if (_value == value && (brush is null || ReferenceEquals(_brush, brush))) return;
        _value = value;
        if (brush is not null) _brush = brush;
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
