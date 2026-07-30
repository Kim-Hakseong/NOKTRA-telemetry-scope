using Avalonia.Media;
using Ts.Core.Analysis;

namespace Ts.App.Controls;

/// <summary>One drawable trace. The chart reads it; it never writes back.</summary>
public interface IChartTrace
{
    string Name { get; }

    string Unit { get; }

    bool IsTraceVisible { get; }

    bool IsSelected { get; }

    Color TraceColor { get; }

    SampleBuffer History { get; }

    /// <summary>Value at the bottom of the plot for this trace.</summary>
    double AxisMin { get; }

    /// <summary>Value at the top of the plot for this trace.</summary>
    double AxisMax { get; }

    /// <summary>Declared valid range, when the definition gives one.</summary>
    double? RangeMin { get; }

    double? RangeMax { get; }
}

/// <summary>
/// Everything the strip chart needs to paint a frame.
///
/// The chart owns no data. It is handed a model, subscribes to one change notification, and reads
/// straight out of the sample ring at paint time — no copy of the series exists for the sake of
/// binding, which is what keeps a hundred thousand samples per trace affordable at 60 fps.
/// </summary>
public interface IChartModel
{
    /// <summary>Raised when the picture would now differ. Marshalled on the UI thread.</summary>
    event EventHandler? Changed;

    IReadOnlyList<IChartTrace> Traces { get; }

    /// <summary>Width of the visible time window.</summary>
    long WindowMicros { get; }

    /// <summary>Time at the right-hand edge of the plot.</summary>
    long RightEdgeMicros { get; }

    /// <summary>False before the first frame arrives, so the chart can say so instead of drawing nothing.</summary>
    bool HasData { get; }

    /// <summary>Cursor position in recording time, or null when the pointer is away.</summary>
    long? CursorMicros { get; }

    /// <summary>Called by the chart as the pointer moves over the plot.</summary>
    void SetCursor(long? micros);
}
