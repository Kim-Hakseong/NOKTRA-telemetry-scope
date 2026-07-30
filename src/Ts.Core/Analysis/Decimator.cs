namespace Ts.Core.Analysis;

/// <summary>
/// The vertical extent of the samples that fall into one bucket. <see cref="HasData"/> is false
/// for a bucket no sample landed in — a gap in the trace, which must be drawn as a gap and not
/// bridged with a straight line that never happened.
/// </summary>
public readonly record struct Envelope(double Min, double Max, bool HasData)
{
    public static Envelope Empty => new(double.NaN, double.NaN, false);

    public double Span => HasData ? Max - Min : 0;
}

/// <summary>
/// Min/max envelope decimation: the reduction a strip chart needs.
///
/// Averaging or sampling every nth point is faster to write and wrong for this job — both erase
/// the single-sample spike that is usually the reason someone is watching. Keeping the minimum and
/// maximum of each bucket preserves every excursion at any zoom level, so a glitch stays visible
/// after a million samples are squeezed into eight hundred pixels.
/// </summary>
public static class Decimator
{
    /// <summary>
    /// Reduces <paramref name="values"/> to <paramref name="buckets"/> envelopes, splitting the
    /// input into equal-count buckets.
    /// </summary>
    public static void Decimate(ReadOnlySpan<double> values, Span<Envelope> buckets)
    {
        if (buckets.Length == 0)
        {
            return;
        }

        for (var b = 0; b < buckets.Length; b++)
        {
            // Boundaries are computed from the bucket index rather than accumulated, so rounding
            // cannot leave a sample in no bucket at all.
            var start = (int)((long)b * values.Length / buckets.Length);
            var end = (int)((long)(b + 1) * values.Length / buckets.Length);

            buckets[b] = Reduce(values[start..end]);
        }
    }

    public static Envelope[] Decimate(ReadOnlySpan<double> values, int bucketCount)
    {
        var buckets = new Envelope[bucketCount];
        Decimate(values, buckets);
        return buckets;
    }

    private static Envelope Reduce(ReadOnlySpan<double> slice)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var any = false;

        foreach (var value in slice)
        {
            if (double.IsNaN(value))
            {
                continue;
            }

            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            any = true;
        }

        return any ? new Envelope(min, max, true) : Envelope.Empty;
    }

    /// <summary>
    /// Reduces the samples inside a time window to one envelope per column, which is how the chart
    /// asks for exactly as much detail as it has pixels for.
    /// </summary>
    /// <returns>True when at least one column has data.</returns>
    public static bool BuildColumns(
        SampleBuffer buffer, long fromMicros, long toMicros, Span<Envelope> columns)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        columns.Clear();
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = Envelope.Empty;
        }

        var span = toMicros - fromMicros;
        if (columns.Length == 0 || span <= 0 || buffer.Count == 0)
        {
            return false;
        }

        var any = false;

        for (var i = buffer.LowerBound(fromMicros); i < buffer.Count; i++)
        {
            var time = buffer.TimeAt(i);
            if (time > toMicros)
            {
                break;
            }

            var column = (int)((time - fromMicros) * columns.Length / span);
            if (column < 0)
            {
                continue;
            }

            // A sample exactly on the right edge belongs to the last column, not past it.
            if (column >= columns.Length)
            {
                column = columns.Length - 1;
            }

            var value = buffer.ValueAt(i);
            if (double.IsNaN(value))
            {
                continue;
            }

            var existing = columns[column];
            columns[column] = existing.HasData
                ? new Envelope(Math.Min(existing.Min, value), Math.Max(existing.Max, value), true)
                : new Envelope(value, value, true);

            any = true;
        }

        return any;
    }
}
