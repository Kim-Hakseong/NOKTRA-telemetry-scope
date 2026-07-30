using System.Globalization;
using System.Text;
using Ts.Core.Decoding;
using Ts.Core.Definition;

namespace Ts.Core.Analysis;

/// <summary>
/// Writes the samples on screen to CSV, so the window someone is looking at can be taken into
/// whatever tool they actually analyse in.
///
/// The output is strictly tabular: a header row and numbers, nothing else. Comment banners and
/// merged annotation columns are what make an export need hand-editing before a spreadsheet or a
/// script will read it. A sample that was not present is an empty cell, never a zero — a zero is
/// a reading.
/// </summary>
public static class CsvExporter
{
    /// <summary>
    /// Exports every channel over a time window. All channels are sampled from the same frames, so
    /// one row is one frame and the columns line up by construction.
    /// </summary>
    /// <returns>Rows written, not counting the header.</returns>
    public static int Write(
        TextWriter writer,
        ChannelSet definition,
        IReadOnlyList<SampleBuffer> histories,
        long fromMicros,
        long toMicros)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(histories);

        if (histories.Count != definition.Channels.Count)
        {
            throw new ArgumentException(
                $"Got {histories.Count} histories for {definition.Channels.Count} channels.",
                nameof(histories));
        }

        WriteHeader(writer, definition);

        if (histories.Count == 0 || histories[0].Count == 0)
        {
            return 0;
        }

        var reference = histories[0];
        var start = reference.LowerBound(fromMicros);
        var rows = 0;
        var line = new StringBuilder(256);

        for (var i = start; i < reference.Count; i++)
        {
            var time = reference.TimeAt(i);
            if (time > toMicros)
            {
                break;
            }

            line.Clear();
            line.Append((time / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture));

            foreach (var history in histories)
            {
                line.Append(',');

                // Histories share a capacity and are filled together, so index i is the same frame
                // in each. The guard is for a channel set that was swapped underneath.
                if (i >= history.Count)
                {
                    continue;
                }

                if (history.StatusAt(i) == SampleStatus.Missing)
                {
                    continue;
                }

                var value = history.ValueAt(i);
                if (!double.IsNaN(value))
                {
                    line.Append(value.ToString("G17", CultureInfo.InvariantCulture));
                }
            }

            writer.Write(line);
            writer.Write('\n');
            rows++;
        }

        return rows;
    }

    public static int WriteFile(
        string path,
        ChannelSet definition,
        IReadOnlyList<SampleBuffer> histories,
        long fromMicros,
        long toMicros)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        return Write(writer, definition, histories, fromMicros, toMicros);
    }

    private static void WriteHeader(TextWriter writer, ChannelSet definition)
    {
        var header = new StringBuilder("time_s");

        foreach (var channel in definition.Channels)
        {
            header.Append(',');
            header.Append(Escape(channel.Label));
        }

        writer.Write(header);
        writer.Write('\n');
    }

    /// <summary>Quotes a field only when it needs it, which keeps the common case readable.</summary>
    private static string Escape(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
