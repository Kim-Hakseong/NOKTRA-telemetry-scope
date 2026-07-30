using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ts.App.Controls;
using Ts.App.Theme;
using Ts.Core.Analysis;
using Ts.Core.Decoding;
using Ts.Core.Definition;

namespace Ts.App.ViewModels;

/// <summary>
/// One channel as the operator sees it: a trace on the scope, a row in the list, and a live
/// read-out.
///
/// The view model holds no copy of the samples. It points at the pipeline's ring buffer, so the
/// list, the chart and the statistics are all reading the same numbers and cannot disagree.
/// </summary>
public sealed partial class ChannelViewModel : ObservableObject, IChartTrace
{
    private readonly ChannelDef _definition;

    public ChannelViewModel(ChannelDef definition, int index, SampleBuffer history)
    {
        _definition = definition;
        Index = index;
        History = history;
        TraceColor = NoktraPalette.TraceColor(index);
        SwatchBrush = NoktraPalette.Frozen(TraceColor);

        AxisMin = definition.Min ?? 0;
        AxisMax = definition.Max ?? 1;
    }

    public int Index { get; }

    public SampleBuffer History { get; }

    public Color TraceColor { get; }

    public IBrush SwatchBrush { get; }

    public string Name => _definition.Name;

    public string Unit => _definition.Unit;

    public string TypeLabel => FieldTypes.ToWireName(_definition.Type).ToUpperInvariant();

    /// <summary>"@12 · S16 BE" — where the number came from, in one line.</summary>
    public string OriginLabel =>
        $"@{_definition.Offset} · {TypeLabel} {(_definition.Endian == Endian.Big ? "BE" : "LE")}";

    public string ScaleLabel => _definition is { A: 1, B: 0 }
        ? "raw"
        : $"x{Format(_definition.A)}{(_definition.B == 0 ? string.Empty : $" {(_definition.B < 0 ? "-" : "+")} {Format(Math.Abs(_definition.B))}")}";

    public double? RangeMin => _definition.Min;

    public double? RangeMax => _definition.Max;

    public bool HasRange => _definition.Min is not null || _definition.Max is not null;

    public string RangeLabel => HasRange
        ? $"{Format(_definition.Min ?? double.NegativeInfinity)} – {Format(_definition.Max ?? double.PositiveInfinity)}{UnitSuffix}"
        : "unbounded";

    private string UnitSuffix => string.IsNullOrEmpty(Unit) ? string.Empty : $" {Unit}";

    [ObservableProperty]
    private bool _isTraceVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private double _axisMin;

    [ObservableProperty]
    private double _axisMax;

    [ObservableProperty]
    private string _valueText = "--";

    [ObservableProperty]
    private string _cursorText = string.Empty;

    [ObservableProperty]
    private bool _isViolating;

    [ObservableProperty]
    private string _statusText = "IDLE";

    [ObservableProperty]
    private string _minText = "--";

    [ObservableProperty]
    private string _maxText = "--";

    [ObservableProperty]
    private string _meanText = "--";

    [ObservableProperty]
    private int _sampleCount;

    /// <summary>Out-of-range samples inside the visible window, not since the session began.</summary>
    [ObservableProperty]
    private int _windowViolationCount;

    /// <summary>
    /// Refreshes the read-outs from the newest sample, and rescales the axis when the channel has
    /// no declared range to scale against.
    /// </summary>
    public void Refresh(long windowFromMicros, long windowToMicros)
    {
        var window = Statistics.Over(History, windowFromMicros, windowToMicros);

        SampleCount = window.Count;
        MinText = window.HasData ? FormatReading(window.Min) : "--";
        MaxText = window.HasData ? FormatReading(window.Max) : "--";
        MeanText = window.HasData ? FormatReading(window.Mean) : "--";
        WindowViolationCount = window.ViolationCount;

        if (History.Count == 0)
        {
            ValueText = "--";
            StatusText = "IDLE";
            IsViolating = false;
            return;
        }

        var latest = History.Latest;
        var status = History.LatestStatus;

        ValueText = FormatReading(latest);
        IsViolating = status is SampleStatus.UnderRange or SampleStatus.OverRange;
        StatusText = status switch
        {
            SampleStatus.UnderRange => "UNDER",
            SampleStatus.OverRange => "OVER",
            SampleStatus.Missing => "SHORT",
            _ => "OK",
        };

        UpdateAxis(windowFromMicros, windowToMicros);
    }

    /// <summary>
    /// A channel with declared limits is drawn against them, so two runs of the same test look
    /// alike rather than each rescaling to its own noise.
    ///
    /// The limits are a floor for the axis, never a ceiling: a reading outside them is exactly the
    /// reading someone needs to see, and an axis that clipped it would hide the fault while the
    /// counter next to it said one had happened. So the declared range and the window's actual
    /// extremes are combined, and whichever is wider wins.
    /// </summary>
    private void UpdateAxis(long fromMicros, long toMicros)
    {
        var min = _definition.Min ?? double.PositiveInfinity;
        var max = _definition.Max ?? double.NegativeInfinity;

        for (var i = History.LowerBound(fromMicros); i < History.Count; i++)
        {
            if (History.TimeAt(i) > toMicros)
            {
                break;
            }

            var value = History.ValueAt(i);
            if (double.IsNaN(value))
            {
                continue;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (double.IsInfinity(min) || double.IsInfinity(max))
        {
            return;
        }

        if (max - min < 1e-9)
        {
            // A flat trace still needs a scale; centre it rather than dividing by zero.
            AxisMin = min - 1;
            AxisMax = max + 1;
            return;
        }

        var headroom = (max - min) * 0.08;
        AxisMin = min - headroom;
        AxisMax = max + headroom;
    }

    /// <summary>Value at the cursor, or empty when the pointer is away from the plot.</summary>
    public void SetCursor(long? micros)
    {
        if (micros is not { } time || History.Count == 0)
        {
            CursorText = string.Empty;
            return;
        }

        var index = History.LowerBound(time);
        if (index >= History.Count)
        {
            index = History.Count - 1;
        }
        else if (index > 0 && time - History.TimeAt(index - 1) < History.TimeAt(index) - time)
        {
            index--;
        }

        CursorText = FormatReading(History.ValueAt(index));
    }

    public string FormatReading(double value) => double.IsNaN(value)
        ? "--"
        : $"{Format(value)}{UnitSuffix}";

    public static string Format(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "+inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        var magnitude = Math.Abs(value);

        return magnitude switch
        {
            0 => "0",
            >= 1_000_000 => value.ToString("0.###e+0", CultureInfo.InvariantCulture),
            >= 1000 => value.ToString("0.#", CultureInfo.InvariantCulture),
            >= 1 => value.ToString("0.###", CultureInfo.InvariantCulture),
            _ => value.ToString("0.#####", CultureInfo.InvariantCulture),
        };
    }
}
