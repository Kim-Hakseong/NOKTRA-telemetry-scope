using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ts.App.Theme;
using Ts.Core.Analysis;

namespace Ts.App.Controls;

/// <summary>
/// The scope.
///
/// Drawn by hand rather than with a charting library, for two reasons that both come from the job
/// rather than from taste. First, the reduction has to be min/max envelope decimation (see
/// <see cref="Decimator"/>) — a general-purpose chart that averages or subsamples would quietly
/// delete the one-sample spike that is usually why someone is watching. Second, traces here carry
/// different units and each needs its own vertical mapping, which is not a chart with several
/// series so much as several charts sharing a time axis.
///
/// Painting reads directly from the sample rings and allocates nothing per frame beyond the
/// geometry it is about to draw.
/// </summary>
public sealed class StripChart : Control
{
    public static readonly StyledProperty<IChartModel?> ModelProperty =
        AvaloniaProperty.Register<StripChart, IChartModel?>(nameof(Model));

    private const double PadLeft = 66;
    private const double PadRight = 16;
    private const double PadTop = 16;
    private const double PadBottom = 28;

    private static readonly Typeface MicroFace = new(FontFamily.Parse("Inter"), weight: FontWeight.Medium);
    private static readonly Typeface MonoFace = new(FontFamily.Parse("monospace"));

    private static readonly IPen GridPen = new Pen(NoktraPalette.LineFaintBrush, 1);
    private static readonly IPen AxisPen = new Pen(NoktraPalette.LineBrush, 1);
    private static readonly IPen CursorPen = new Pen(NoktraPalette.InkSoftBrush, 1)
    {
        DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
    };

    private static readonly IPen RangePen = new Pen(NoktraPalette.AlertBrush, 1)
    {
        DashStyle = new DashStyle(new double[] { 2, 4 }, 0),
    };

    private static readonly IPen ViolationPen = new Pen(NoktraPalette.AlertBrush, 2.6);

    private static readonly IPen ViolationPenFaint = new Pen(
        NoktraPalette.Frozen(Color.FromArgb(0x9A, 0xA8, 0x41, 0x2F)), 1.6);

    /// <summary>Territory the selected channel is not supposed to reach. Barely there on purpose.</summary>
    private static readonly IBrush ForbiddenBrush =
        NoktraPalette.Frozen(Color.FromArgb(0x0E, 0xA8, 0x41, 0x2F));

    private Envelope[] _columns = Array.Empty<Envelope>();
    private IChartModel? _subscribed;

    public StripChart()
    {
        ClipToBounds = true;
    }

    public IChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty)
        {
            Resubscribe(change.GetNewValue<IChartModel?>());
            InvalidateVisual();
        }
    }

    private void Resubscribe(IChartModel? model)
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnModelChanged;
        }

        _subscribed = model;

        if (_subscribed is not null)
        {
            _subscribed.Changed += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var model = Model;
        if (model is null)
        {
            return;
        }

        var plot = PlotRect();
        var point = e.GetPosition(this);

        model.SetCursor(plot.Contains(point) ? TimeAt(point.X, plot, model) : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Model?.SetCursor(null);
    }

    private Rect PlotRect()
    {
        var width = Math.Max(0, Bounds.Width - PadLeft - PadRight);
        var height = Math.Max(0, Bounds.Height - PadTop - PadBottom);
        return new Rect(PadLeft, PadTop, width, height);
    }

    private static long TimeAt(double x, Rect plot, IChartModel model)
    {
        var fraction = plot.Width <= 0 ? 0 : (x - plot.X) / plot.Width;
        return model.RightEdgeMicros - (long)((1 - fraction) * model.WindowMicros);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(NoktraPalette.PanelSunkBrush, null, bounds, 7, 7);

        var model = Model;
        var plot = PlotRect();
        if (model is null || plot.Width < 8 || plot.Height < 8)
        {
            return;
        }

        var from = model.RightEdgeMicros - model.WindowMicros;
        var to = model.RightEdgeMicros;

        DrawGrid(context, plot, model, from, to);

        var selected = SelectedTrace(model);
        if (selected is not null)
        {
            DrawForbiddenBands(context, plot, selected);
            DrawValueAxis(context, plot, selected);
        }

        var columnCount = Math.Max(1, (int)plot.Width);
        if (_columns.Length != columnCount)
        {
            _columns = new Envelope[columnCount];
        }

        foreach (var trace in model.Traces)
        {
            if (trace.IsTraceVisible)
            {
                DrawTrace(context, plot, trace, from, to);
            }
        }

        if (!model.HasData)
        {
            DrawGhost(context, plot, "NO SIGNAL");
        }

        DrawCursor(context, plot, model, from, to);
    }

    private static IChartTrace? SelectedTrace(IChartModel model)
    {
        IChartTrace? firstVisible = null;

        foreach (var trace in model.Traces)
        {
            if (!trace.IsTraceVisible)
            {
                continue;
            }

            if (trace.IsSelected)
            {
                return trace;
            }

            firstVisible ??= trace;
        }

        return firstVisible;
    }

    private void DrawGrid(DrawingContext context, Rect plot, IChartModel model, long from, long to)
    {
        const int verticalDivisions = 6;
        const int horizontalDivisions = 4;

        for (var i = 0; i <= horizontalDivisions; i++)
        {
            var y = Snap(plot.Y + (plot.Height * i / horizontalDivisions));
            context.DrawLine(i == horizontalDivisions ? AxisPen : GridPen,
                new Point(plot.X, y), new Point(plot.Right, y));
        }

        for (var i = 0; i <= verticalDivisions; i++)
        {
            var x = Snap(plot.X + (plot.Width * i / verticalDivisions));
            context.DrawLine(i == 0 ? AxisPen : GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));

            // Time labels count back from the right edge, which is "now" on a live trace and the
            // playhead on a replayed one. Absolute times would be meaningless in both cases.
            var secondsBack = (to - from) * (verticalDivisions - i) / verticalDivisions / 1_000_000.0;
            var label = secondsBack == 0 ? "0" : $"-{FormatSeconds(secondsBack)}";

            var text = Micro(label, NoktraPalette.MutedBrush);
            context.DrawText(text, new Point(x - (text.Width / 2), plot.Bottom + 8));
        }

        var caption = Micro("SECONDS", NoktraPalette.MutedBrush);
        context.DrawText(caption, new Point(plot.Right - caption.Width, plot.Bottom + 18));
    }

    private static string FormatSeconds(double seconds) => seconds >= 10
        ? seconds.ToString("0", CultureInfo.InvariantCulture)
        : seconds.ToString("0.#", CultureInfo.InvariantCulture);

    private void DrawValueAxis(DrawingContext context, Rect plot, IChartTrace trace)
    {
        const int divisions = 4;
        var (min, max) = AxisRange(trace);

        for (var i = 0; i <= divisions; i++)
        {
            var value = max - ((max - min) * i / divisions);
            var y = plot.Y + (plot.Height * i / divisions);

            var text = Mono(FormatValue(value), NoktraPalette.InkSoftBrush);
            context.DrawText(text, new Point(plot.X - 8 - text.Width, y - (text.Height / 2)));
        }

        var unit = Micro(
            string.IsNullOrEmpty(trace.Unit) ? trace.Name.ToUpperInvariant() : trace.Unit.ToUpperInvariant(),
            NoktraPalette.AccentBrush);
        context.DrawText(unit, new Point(PadLeft - 8 - unit.Width, plot.Y - 12));

        // The declared limits, drawn on the axis they belong to. A reading that leaves them is
        // flagged in the channel list as well — colour alone should not carry the fact.
        DrawLimit(context, plot, trace, trace.RangeMin);
        DrawLimit(context, plot, trace, trace.RangeMax);
    }

    private void DrawLimit(DrawingContext context, Rect plot, IChartTrace trace, double? limit)
    {
        if (limit is not { } value)
        {
            return;
        }

        var (min, max) = AxisRange(trace);
        if (value < min || value > max)
        {
            return;
        }

        var y = Snap(MapY(value, plot, min, max));
        context.DrawLine(RangePen, new Point(plot.X, y), new Point(plot.Right, y));
    }

    /// <summary>
    /// Shades the parts of the plot that lie outside the selected channel's declared range, so an
    /// excursion is visible as territory the trace should not be in even before the numbers are
    /// read. Very light: it must not compete with the data drawn on top of it.
    /// </summary>
    private void DrawForbiddenBands(DrawingContext context, Rect plot, IChartTrace trace)
    {
        var (min, max) = AxisRange(trace);

        if (trace.RangeMax is { } upper && upper < max)
        {
            var y = MapY(upper, plot, min, max);
            context.FillRectangle(ForbiddenBrush, new Rect(plot.X, plot.Y, plot.Width, y - plot.Y));
        }

        if (trace.RangeMin is { } lower && lower > min)
        {
            var y = MapY(lower, plot, min, max);
            context.FillRectangle(ForbiddenBrush, new Rect(plot.X, y, plot.Width, plot.Bottom - y));
        }
    }

    private void DrawTrace(DrawingContext context, Rect plot, IChartTrace trace, long from, long to)
    {
        var columns = _columns.AsSpan();
        if (!Decimator.BuildColumns(trace.History, from, to, columns))
        {
            return;
        }

        var (min, max) = AxisRange(trace);

        // Each trace is drawn against its own range, because channels here carry different units
        // and a shared axis would flatten most of them into a line. Only one trace can own the
        // labelled axis, so the selected one is drawn heavier and at full strength and the rest
        // step back — that is what ties the numbers on the left to a particular curve.
        var pen = new Pen(
            new SolidColorBrush(trace.TraceColor, trace.IsSelected ? 1.0 : 0.55).ToImmutable(),
            trace.IsSelected ? 1.9 : 1.1,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            var open = false;

            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i];
                if (!column.HasData)
                {
                    // A gap in the data is drawn as a gap. Bridging it would invent a reading.
                    if (open)
                    {
                        sink.EndFigure(false);
                        open = false;
                    }

                    continue;
                }

                var x = plot.X + i + 0.5;
                var high = MapY(column.Max, plot, min, max);
                var low = MapY(column.Min, plot, min, max);

                if (!open)
                {
                    sink.BeginFigure(new Point(x, high), false);
                    open = true;
                }
                else
                {
                    sink.LineTo(new Point(x, high));
                }

                if (low != high)
                {
                    sink.LineTo(new Point(x, low));
                }
            }

            if (open)
            {
                sink.EndFigure(false);
            }
        }

        context.DrawGeometry(null, pen, geometry);
        DrawViolations(context, plot, trace, columns, min, max);
    }

    /// <summary>
    /// Over-draws the part of the trace that left its declared range, in the alert colour.
    ///
    /// Only the offending segment is recoloured, not the whole curve, so the shape stays readable
    /// and the excursion is located in time as well as flagged. Colour is not the only signal: the
    /// channel row carries an UNDER or OVER label and the read-out counts the samples, because an
    /// operator who cannot separate red from grey still has to be able to work.
    /// </summary>
    private void DrawViolations(
        DrawingContext context, Rect plot, IChartTrace trace, ReadOnlySpan<Envelope> columns,
        double axisMin, double axisMax)
    {
        if (trace.RangeMin is null && trace.RangeMax is null)
        {
            return;
        }

        var lower = trace.RangeMin ?? double.NegativeInfinity;
        var upper = trace.RangeMax ?? double.PositiveInfinity;
        var pen = trace.IsSelected ? ViolationPen : ViolationPenFaint;

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (!column.HasData)
            {
                continue;
            }

            var x = plot.X + i + 0.5;

            if (column.Max > upper)
            {
                context.DrawLine(pen,
                    new Point(x, MapY(column.Max, plot, axisMin, axisMax)),
                    new Point(x, MapY(Math.Max(upper, column.Min), plot, axisMin, axisMax)));
            }

            if (column.Min < lower)
            {
                context.DrawLine(pen,
                    new Point(x, MapY(Math.Min(lower, column.Max), plot, axisMin, axisMax)),
                    new Point(x, MapY(column.Min, plot, axisMin, axisMax)));
            }
        }
    }

    private void DrawCursor(DrawingContext context, Rect plot, IChartModel model, long from, long to)
    {
        if (model.CursorMicros is not { } cursor || cursor < from || cursor > to || to <= from)
        {
            return;
        }

        var x = Snap(plot.X + ((double)(cursor - from) / (to - from) * plot.Width));
        context.DrawLine(CursorPen, new Point(x, plot.Y), new Point(x, plot.Bottom));

        var label = Mono($"-{FormatSeconds((to - cursor) / 1_000_000.0)}s", NoktraPalette.OnInkBrush);
        var box = new Rect(x + 4, plot.Y + 4, label.Width + 12, label.Height + 6);

        if (box.Right > plot.Right)
        {
            box = box.WithX(x - box.Width - 4);
        }

        context.DrawRectangle(NoktraPalette.InkBrush, null, box, 4, 4);
        context.DrawText(label, new Point(box.X + 6, box.Y + 3));
    }

    private void DrawGhost(DrawingContext context, Rect plot, string text)
    {
        var ghost = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Parse("Inter"), weight: FontWeight.Bold),
            46, NoktraPalette.Frozen(Color.FromArgb(0x1A, 0, 0, 0)));

        context.DrawText(ghost, new Point(
            plot.X + ((plot.Width - ghost.Width) / 2),
            plot.Y + ((plot.Height - ghost.Height) / 2)));
    }

    private static (double Min, double Max) AxisRange(IChartTrace trace)
    {
        var min = trace.AxisMin;
        var max = trace.AxisMax;

        if (double.IsNaN(min) || double.IsNaN(max) || max <= min)
        {
            // A flat or unknown trace still needs a scale to be drawn against; centre it.
            var centre = double.IsNaN(min) ? 0 : min;
            return (centre - 1, centre + 1);
        }

        return (min, max);
    }

    private static double MapY(double value, Rect plot, double min, double max)
    {
        var fraction = (value - min) / (max - min);
        var y = plot.Bottom - (fraction * plot.Height);
        return Math.Clamp(y, plot.Y - 2, plot.Bottom + 2);
    }

    private static string FormatValue(double value)
    {
        var magnitude = Math.Abs(value);

        return magnitude switch
        {
            0 => "0",
            >= 100000 => value.ToString("0.##e+0", CultureInfo.InvariantCulture),
            >= 100 => value.ToString("0", CultureInfo.InvariantCulture),
            >= 1 => value.ToString("0.0", CultureInfo.InvariantCulture),
            _ => value.ToString("0.000", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Half-pixel offset so a one-pixel line lands on a pixel instead of straddling two.</summary>
    private static double Snap(double value) => Math.Round(value) + 0.5;

    private static FormattedText Micro(string text, IBrush brush) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MicroFace, 9, brush);

    private static FormattedText Mono(string text, IBrush brush) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 10, brush);
}
