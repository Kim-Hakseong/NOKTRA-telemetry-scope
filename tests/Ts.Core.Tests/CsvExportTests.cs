using System.Globalization;
using Ts.Core.Analysis;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Xunit;

namespace Ts.Core.Tests;

public class CsvExportTests
{
    private static ChannelSet Definition => ChannelSetReader.Read("""
        name: Export vector
        framing:
          mode: fixed
          frameLength: 4
        channels:
          - name: Current
            offset: 0
            type: u16
            a: 0.001
            unit: mA
            min: 4
            max: 20
          - name: Counter
            offset: 2
            type: u16
        """);

    private static TelemetryPipeline Filled(int frames)
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 1000);

        for (var i = 0; i < frames; i++)
        {
            var current = (ushort)(4000 + (i * 100));
            var frame = new byte[]
            {
                (byte)(current >> 8), (byte)current, (byte)(i >> 8), (byte)i,
            };

            pipeline.Accept(i * 1000, frame);
        }

        return pipeline;
    }

    private static string Export(TelemetryPipeline pipeline, long from, long to)
    {
        var writer = new StringWriter { NewLine = "\n" };
        CsvExporter.Write(writer, pipeline.Definition, pipeline.Histories, from, to);
        return writer.ToString();
    }

    [Fact]
    public void HeaderNamesEveryChannelWithItsUnit()
    {
        var lines = Export(Filled(3), 0, 10_000).Split('\n');

        Assert.Equal("time_s,Current [mA],Counter", lines[0]);
    }

    [Fact]
    public void OneRowPerFrameInTheWindow()
    {
        var csv = Export(Filled(10), 3000, 5000);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length); // header plus frames 3, 4 and 5

        var row = lines[1].Split(',');
        Assert.Equal(0.003, double.Parse(row[0], CultureInfo.InvariantCulture), 9);
        Assert.Equal(4.3, double.Parse(row[1], CultureInfo.InvariantCulture), 9);
        Assert.Equal(3, double.Parse(row[2], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ValuesRoundTripAtFullPrecision()
    {
        var pipeline = Filled(5);
        var csv = Export(pipeline, 0, 10_000);
        var row = csv.Split('\n')[1].Split(',');

        Assert.Equal(pipeline.Histories[0].ValueAt(0), double.Parse(row[1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AMissingSampleIsAnEmptyCellNotAZero()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 10);
        pipeline.Accept(0, new byte[] { 0x0F, 0xA0 }); // too short for Counter

        var row = Export(pipeline, 0, 1000).Split('\n')[1];

        Assert.Equal("0.000000,4,", row);
    }

    [Fact]
    public void AnEmptyHistoryStillProducesAUsableFile()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 10);

        var csv = Export(pipeline, 0, 1000);

        Assert.Equal("time_s,Current [mA],Counter\n", csv);
    }

    [Fact]
    public void ChannelNamesContainingACommaAreQuoted()
    {
        var set = ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 2
            channels:
              - name: "Pitch, deg"
                offset: 0
                type: u16
            """);

        var pipeline = new TelemetryPipeline(set, historyCapacity: 4);
        var writer = new StringWriter { NewLine = "\n" };
        CsvExporter.Write(writer, set, pipeline.Histories, 0, 1000);

        Assert.StartsWith("time_s,\"Pitch, deg\"", writer.ToString());
    }

    [Fact]
    public void WritesTheFileAndReportsTheRowCount()
    {
        var directory = Directory.CreateTempSubdirectory("tsr-csv-");
        try
        {
            var path = Path.Combine(directory.FullName, "window.csv");
            var pipeline = Filled(6);

            var rows = CsvExporter.WriteFile(path, pipeline.Definition, pipeline.Histories, 0, 5000);

            Assert.Equal(6, rows);
            Assert.Equal(7, File.ReadAllLines(path).Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
