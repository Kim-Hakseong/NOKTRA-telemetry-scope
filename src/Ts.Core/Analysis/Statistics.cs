using Ts.Core.Decoding;

namespace Ts.Core.Analysis;

/// <summary>What one channel did over a stretch of time.</summary>
public readonly record struct WindowStatistics(
    int Count,
    double Min,
    double Max,
    double Mean,
    double Latest,
    int ViolationCount,
    int MissingCount)
{
    public static WindowStatistics Empty =>
        new(0, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);

    public bool HasData => Count > 0;

    public double Span => HasData ? Max - Min : 0;
}

/// <summary>
/// Summarises the samples inside a time window.
///
/// The window is the one on screen, not the whole session: "what is this channel doing" is a
/// question about the part being looked at, and a mean dragged around by a warm-up half an hour
/// ago answers a question nobody asked.
/// </summary>
public static class Statistics
{
    public static WindowStatistics Over(SampleBuffer buffer, long fromMicros, long toMicros)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var count = 0;
        var violations = 0;
        var missing = 0;
        var latest = double.NaN;

        // Neumaier summation: a mean over a few hundred thousand samples of similar magnitude
        // loses real precision to naive accumulation, and the mean is what people compare between
        // runs.
        var sum = 0.0;
        var compensation = 0.0;

        for (var i = buffer.LowerBound(fromMicros); i < buffer.Count; i++)
        {
            if (buffer.TimeAt(i) > toMicros)
            {
                break;
            }

            var status = buffer.StatusAt(i);
            if (status == SampleStatus.Missing)
            {
                missing++;
                continue;
            }

            var value = buffer.ValueAt(i);
            if (double.IsNaN(value))
            {
                missing++;
                continue;
            }

            if (status is SampleStatus.UnderRange or SampleStatus.OverRange)
            {
                violations++;
            }

            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            var updated = sum + value;
            compensation += Math.Abs(sum) >= Math.Abs(value)
                ? (sum - updated) + value
                : (value - updated) + sum;
            sum = updated;

            latest = value;
            count++;
        }

        return count == 0
            ? WindowStatistics.Empty with { MissingCount = missing }
            : new WindowStatistics(
                count, min, max, (sum + compensation) / count, latest, violations, missing);
    }
}
