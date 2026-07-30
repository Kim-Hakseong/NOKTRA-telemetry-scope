using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ts.App.Controls;
using Ts.Core.Analysis;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Recording;
using Ts.Core.Replay;
using Ts.Core.Time;
using Ts.Core.Transport;

namespace Ts.App.ViewModels;

public enum TransportState
{
    Idle,
    Running,
    Paused,
}

/// <summary>
/// The application.
///
/// Frames arrive on whatever thread produced them and go straight into a bounded queue; this class
/// drains that queue on a fixed 50 ms beat and does all decoding, read-out formatting and
/// repainting in one place. Sixty repaints a second of a chart that changed once is wasted work,
/// and a repaint per frame at ten thousand frames a second is an unusable application — one beat
/// is both the cheapest and the smoothest option.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IChartModel
{
    /// <summary>The UI beat. Fast enough to read as live, slow enough to batch a burst.</summary>
    private const int TickMilliseconds = 50;

    /// <summary>Ceiling on frames decoded per beat, so a backlog cannot freeze the window.</summary>
    private const int MaxFramesPerTick = 50_000;

    private readonly FrameQueue _queue = new();
    private readonly List<CapturedFrame> _batch = new(4096);
    private readonly DispatcherTimer _timer;
    private readonly IFileDialogs _dialogs;

    private TelemetryPipeline? _pipeline;
    private TelemetrySource? _source;
    private TsrWriter? _recorder;
    private TsrFile? _recording;
    private ReplayEngine? _engine;
    private CancellationTokenSource? _replayCancellation;
    private long _frozenEdgeMicros;
    private bool _updatingPosition;

    /// <summary>Record to resume from: set by pausing, seeking or previewing.</summary>
    private int _engineResumeIndex;

    public MainWindowViewModel(IFileDialogs dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        RefreshSerialPorts();

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(TickMilliseconds), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    public event EventHandler? Changed;

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public IReadOnlyList<IChartTrace> Traces => Channels;

    /// <summary>Time windows offered on the scope, in seconds.</summary>
    public IReadOnlyList<string> TimeWindows { get; } =
        new[] { "5 s", "10 s", "30 s", "1 min", "5 min" };

    private static readonly long[] WindowMicrosByIndex =
    {
        5_000_000, 10_000_000, 30_000_000, 60_000_000, 300_000_000,
    };

    public IReadOnlyList<string> SpeedLabels { get; } =
        new[] { "0.1x", "0.25x", "0.5x", "1x", "2x", "4x", "10x" };

    private static readonly double[] SpeedByIndex = { 0.1, 0.25, 0.5, 1.0, 2.0, 4.0, 10.0 };

    // --- definition

    [ObservableProperty]
    private string _definitionName = "No definition";

    [ObservableProperty]
    private string _definitionPath = string.Empty;

    [ObservableProperty]
    private string _framingSummary = "--";

    /// <summary>Where the definition in use came from — a file, or the recording's own copy.</summary>
    [ObservableProperty]
    private string _definitionOrigin = "NONE LOADED";

    [ObservableProperty]
    private bool _hasDefinition;

    // --- scope

    [ObservableProperty]
    private int _timeWindowIndex = 1;

    [ObservableProperty]
    private bool _isFrozen;

    [ObservableProperty]
    private string _cursorCaption = string.Empty;

    [ObservableProperty]
    private ChannelViewModel? _selectedChannel;

    // --- live source

    public IReadOnlyList<string> SourceKinds { get; } = new[] { "UDP", "Serial" };

    public IReadOnlyList<string> BaudRates { get; } =
        new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" };

    [ObservableProperty]
    private ObservableCollection<string> _serialPorts = new();

    [ObservableProperty]
    private int _sourceKindIndex;

    [ObservableProperty]
    private string _udpHost = "0.0.0.0";

    [ObservableProperty]
    private string _udpPort = "5005";

    [ObservableProperty]
    private string? _selectedSerialPort;

    [ObservableProperty]
    private int _baudRateIndex = 4;

    [ObservableProperty]
    private bool _isReceiving;

    [ObservableProperty]
    private string _sourceDescription = "Not connected";

    [ObservableProperty]
    private string _discardedText = "0";

    // --- recording

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingTarget = "Not recording";

    // --- replay transport

    [ObservableProperty]
    private bool _hasRecording;

    [ObservableProperty]
    private string _recordingName = "No recording";

    [ObservableProperty]
    private string _recordingSummary = "--";

    [ObservableProperty]
    private int _speedIndex = 3;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds = 1;

    [ObservableProperty]
    private TransportState _replayState = TransportState.Idle;

    // --- counters

    [ObservableProperty]
    private string _frameCountText = "0";

    [ObservableProperty]
    private string _byteCountText = "0 B";

    [ObservableProperty]
    private string _rateText = "0 /s";

    [ObservableProperty]
    private string _violationText = "0";

    [ObservableProperty]
    private string _droppedText = "0";

    [ObservableProperty]
    private string _statusMessage = "Open a channel definition or a recording to begin.";

    [ObservableProperty]
    private bool _statusIsError;

    public string ReplayStateLabel => ReplayState switch
    {
        TransportState.Running => "PLAYING",
        TransportState.Paused => "PAUSED",
        _ => "STOPPED",
    };

    public bool IsReplaying => ReplayState == TransportState.Running;

    public bool IsUdpSource => SourceKindIndex == 0;

    public bool IsSerialSource => SourceKindIndex == 1;

    public string ConnectLabel => IsReceiving ? "Disconnect" : "Connect";

    public string RecordLabel => IsRecording ? "Stop recording" : "Record";

    partial void OnSourceKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsUdpSource));
        OnPropertyChanged(nameof(IsSerialSource));
    }

    partial void OnIsReceivingChanged(bool value) => OnPropertyChanged(nameof(ConnectLabel));

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordLabel));

    partial void OnReplayStateChanged(TransportState value)
    {
        OnPropertyChanged(nameof(ReplayStateLabel));
        OnPropertyChanged(nameof(IsReplaying));
    }

    partial void OnSpeedIndexChanged(int value)
    {
        if (_engine is not null)
        {
            _engine.Speed = SpeedByIndex[Math.Clamp(value, 0, SpeedByIndex.Length - 1)];
        }
    }

    partial void OnTimeWindowIndexChanged(int value)
    {
        // A paused recording only has the previewed window in memory, so widening the view has to
        // go back to the file for the rest. Otherwise the axis says thirty seconds and the plot
        // shows the ten that happen to be loaded.
        if (_recording is not null && ReplayState != TransportState.Running)
        {
            PreviewUpTo((long)(PositionSeconds * 1_000_000));
            return;
        }

        // The statistics are over the visible window, so widening or narrowing it changes them.
        // Repainting without recomputing would put a mean on screen that belongs to a window the
        // chart is no longer showing.
        RefreshReadouts();
        RaiseChanged();
    }

    partial void OnIsFrozenChanged(bool value)
    {
        if (value)
        {
            _frozenEdgeMicros = CurrentEdgeMicros;
        }

        RefreshReadouts();
        RaiseChanged();
    }

    partial void OnSelectedChannelChanged(ChannelViewModel? value)
    {
        // Selection decides which channel owns the value axis, so the chart has to repaint.
        foreach (var channel in Channels)
        {
            channel.IsSelected = ReferenceEquals(channel, value);
        }

        RaiseChanged();
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (_updatingPosition || _recording is null)
        {
            return;
        }

        SeekTo((long)(value * 1_000_000));
    }

    // --- IChartModel

    public long WindowMicros => WindowMicrosByIndex[Math.Clamp(TimeWindowIndex, 0, WindowMicrosByIndex.Length - 1)];

    public long RightEdgeMicros => IsFrozen ? _frozenEdgeMicros : CurrentEdgeMicros;

    public bool HasData => _pipeline is { FrameCount: > 0 };

    public long? CursorMicros { get; private set; }

    /// <summary>
    /// A live scope keeps scrolling when the data stops — that gap is the symptom. A replay's edge
    /// is the playhead instead, because there is no "now" in a file.
    /// </summary>
    private long CurrentEdgeMicros => _source is { IsRunning: true } source
        ? Math.Max(source.ElapsedMicros, _pipeline?.LastFrameMicros ?? 0)
        : _pipeline?.LastFrameMicros ?? 0;

    public void SetCursor(long? micros)
    {
        if (CursorMicros == micros)
        {
            return;
        }

        CursorMicros = micros;

        foreach (var channel in Channels)
        {
            channel.SetCursor(micros);
        }

        CursorCaption = micros is { } time && _pipeline is not null
            ? $"CURSOR {(RightEdgeMicros - time) / 1_000_000.0:0.000} s BACK"
            : string.Empty;

        RaiseChanged();
    }

    // --- commands

    [RelayCommand]
    private async Task OpenDefinitionAsync()
    {
        var path = await _dialogs.OpenAsync("Open channel definition", FileFilter.Definitions);
        if (path is null)
        {
            return;
        }

        try
        {
            LoadDefinition(ChannelSetReader.ReadFile(path), path);
            Report($"Loaded {Path.GetFileName(path)}: {Channels.Count} channels.", error: false);
        }
        catch (Exception ex) when (ex is DefinitionException or IOException)
        {
            Report(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task OpenRecordingAsync()
    {
        var path = await _dialogs.OpenAsync("Open recording", FileFilter.Recordings);
        if (path is null)
        {
            return;
        }

        try
        {
            LoadRecording(TsrReader.ReadFile(path));
        }
        catch (Exception ex) when (ex is TsrFormatException or DefinitionException or IOException)
        {
            Report(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_recording is null)
        {
            return;
        }

        if (ReplayState == TransportState.Running)
        {
            _engineResumeIndex = _engine?.Position ?? 0;
            StopReplayTask();
            ReplayState = TransportState.Paused;
            Report("Replay paused.", error: false);
            return;
        }

        StartReplay(_engineResumeIndex);
    }

    [RelayCommand]
    private void StopReplay()
    {
        if (_recording is null)
        {
            return;
        }

        StopReplayTask();
        ReplayState = TransportState.Idle;

        _pipeline?.Reset();
        _queue.Clear();
        _engineResumeIndex = 0;
        UpdatePosition(0);
        RefreshReadouts();
        RaiseChanged();
        Report("Replay stopped.", error: false);
    }

    [RelayCommand]
    private void ToggleFreeze() => IsFrozen = !IsFrozen;

    /// <summary>
    /// Exports exactly the window on screen. Exporting the whole session instead would be a
    /// different, larger answer to the question the operator asked by choosing a window — and on a
    /// long run, one nobody can open.
    /// </summary>
    [RelayCommand]
    private async Task ExportWindowAsync()
    {
        if (_pipeline is null || _pipeline.FrameCount == 0)
        {
            Report("There is nothing on screen to export.", error: true);
            return;
        }

        var to = RightEdgeMicros;
        var from = to - WindowMicros;

        var suggested = $"{Sanitise(_pipeline.Definition.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        var path = await _dialogs.SaveAsync("Export visible window", suggested, FileFilter.CommaSeparated);
        if (path is null)
        {
            return;
        }

        try
        {
            var rows = CsvExporter.WriteFile(
                path, _pipeline.Definition, _pipeline.Histories, from, to);

            Report($"Exported {rows:N0} rows to {Path.GetFileName(path)}.", error: false);
        }
        catch (IOException ex)
        {
            Report($"Could not export: {ex.Message}", error: true);
        }
    }

    // --- live source

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        var current = SelectedSerialPort;

        SerialPorts = new ObservableCollection<string>(SerialSource.AvailablePorts());
        SelectedSerialPort = current is not null && SerialPorts.Contains(current)
            ? current
            : SerialPorts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsReceiving)
        {
            await DisconnectAsync().ConfigureAwait(true);
            return;
        }

        if (_pipeline is null)
        {
            Report("Open a channel definition before connecting.", error: true);
            return;
        }

        try
        {
            var source = BuildSource(_pipeline.Definition);

            // Bind before declaring success, so "port already in use" is reported here rather than
            // as a silent failure on a background thread a moment later.
            if (source is UdpSource udp)
            {
                udp.Bind();
            }

            source.Failed += OnSourceFailed;

            _pipeline.Reset();
            _queue.Clear();
            CursorMicros = null;

            source.Start();
            _source = source;

            IsReceiving = true;
            SourceDescription = source.Description;
            Report($"Receiving on {source.Description}.", error: false);
            RaiseChanged();
        }
        catch (Exception ex) when (ex is SocketException or IOException
                                       or UnauthorizedAccessException or DefinitionException
                                       or FormatException or ArgumentException)
        {
            Report($"Could not connect: {ex.Message}", error: true);
        }
    }

    private async Task DisconnectAsync()
    {
        var source = _source;
        _source = null;

        if (source is not null)
        {
            source.Failed -= OnSourceFailed;
            await source.StopAsync().ConfigureAwait(true);
            source.Dispose();
        }

        IsReceiving = false;
        SourceDescription = "Not connected";
        Report("Disconnected.", error: false);
        RaiseChanged();
    }

    private TelemetrySource BuildSource(ChannelSet set)
    {
        if (SourceKindIndex == 1)
        {
            if (string.IsNullOrWhiteSpace(SelectedSerialPort))
            {
                throw new DefinitionException("Choose a serial port first.");
            }

            var serial = new SourceDef
            {
                Kind = SourceKind.Serial,
                PortName = SelectedSerialPort,
                BaudRate = int.Parse(BaudRates[BaudRateIndex], CultureInfo.InvariantCulture),
                DataBits = set.Source.DataBits,
                Parity = set.Source.Parity,
                StopBits = set.Source.StopBits,
            };

            return new SerialSource(
                new ChannelSet
                {
                    Name = set.Name,
                    Framing = set.Framing,
                    Channels = set.Channels,
                    Source = serial,
                },
                _queue,
                SystemClock.Instance);
        }

        var udp = new SourceDef
        {
            Kind = SourceKind.Udp,
            Host = string.IsNullOrWhiteSpace(UdpHost) ? "0.0.0.0" : UdpHost.Trim(),
            Port = int.Parse(UdpPort.Trim(), CultureInfo.InvariantCulture),
            DatagramPerFrame = set.Source.DatagramPerFrame,
        };

        return new UdpSource(set.Framing, udp, _queue, SystemClock.Instance);
    }

    private void OnSourceFailed(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsReceiving = false;
            Report($"Source stopped: {message}", error: true);
        });

    // --- recording

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            StopRecording();
            return;
        }

        if (_pipeline is null)
        {
            Report("Open a channel definition before recording.", error: true);
            return;
        }

        var suggested = $"{Sanitise(_pipeline.Definition.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.tsr";
        var path = await _dialogs.SaveAsync("Record to file", suggested, FileFilter.Recordings);
        if (path is null)
        {
            return;
        }

        try
        {
            _recorder = TsrWriter.Create(path, _pipeline.Definition, SystemClock.UnixNowMicros);
            _pipeline.Recorder = _recorder;

            IsRecording = true;
            RecordingTarget = Path.GetFileName(path);
            Report($"Recording to {RecordingTarget}.", error: false);
        }
        catch (IOException ex)
        {
            Report($"Could not record: {ex.Message}", error: true);
        }
    }

    private void StopRecording()
    {
        var recorder = _recorder;
        _recorder = null;

        if (_pipeline is not null)
        {
            _pipeline.Recorder = null;
        }

        if (recorder is null)
        {
            return;
        }

        var count = recorder.RecordCount;
        var bytes = recorder.BytesWritten;
        recorder.Dispose();

        IsRecording = false;
        RecordingTarget = "Not recording";
        Report($"Recorded {count:N0} frames ({FormatBytes(bytes)}).", error: false);
    }

    private static string Sanitise(string name)
    {
        var cleaned = new string(name
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }

        return cleaned.Length == 0 ? "capture" : cleaned;
    }

    [RelayCommand]
    private void ShowAllChannels()
    {
        foreach (var channel in Channels)
        {
            channel.IsTraceVisible = true;
        }

        RaiseChanged();
    }

    [RelayCommand]
    private void HideAllChannels()
    {
        foreach (var channel in Channels)
        {
            channel.IsTraceVisible = false;
        }

        RaiseChanged();
    }

    // --- loading

    public void LoadDefinition(ChannelSet set, string path)
    {
        StopReplayTask();
        ReplayState = TransportState.Idle;

        _pipeline = new TelemetryPipeline(set);
        _queue.Clear();

        foreach (var existing in Channels)
        {
            existing.PropertyChanged -= OnChannelPropertyChanged;
        }

        Channels.Clear();
        for (var i = 0; i < set.Channels.Count; i++)
        {
            var channel = new ChannelViewModel(set.Channels[i], i, _pipeline.Histories[i]);
            channel.PropertyChanged += OnChannelPropertyChanged;
            Channels.Add(channel);
        }

        SelectedChannel = Channels.FirstOrDefault();

        DefinitionPath = path;
        DefinitionOrigin = $"FILE · {set.Channels.Count} CHANNELS";
        DefinitionName = set.Name;
        FramingSummary = Describe(set.Framing);
        HasDefinition = true;
        CursorMicros = null;

        // The definition states where its data normally comes from; offer that rather than making
        // the operator retype a port that is already written down.
        if (set.Source.Kind == SourceKind.Udp)
        {
            SourceKindIndex = 0;
            UdpHost = set.Source.Host;
            UdpPort = set.Source.Port.ToString(CultureInfo.InvariantCulture);
        }
        else if (set.Source.Kind == SourceKind.Serial)
        {
            SourceKindIndex = 1;

            if (!string.IsNullOrEmpty(set.Source.PortName) && SerialPorts.Contains(set.Source.PortName))
            {
                SelectedSerialPort = set.Source.PortName;
            }

            var baud = set.Source.BaudRate.ToString(CultureInfo.InvariantCulture);
            var index = BaudRates.ToList().IndexOf(baud);
            if (index >= 0)
            {
                BaudRateIndex = index;
            }
        }

        RefreshReadouts();
        RaiseChanged();
    }

    public void LoadRecording(TsrFile file)
    {
        var set = file.ReadDefinition();
        LoadDefinition(set, file.Path);

        // The definition in force is the one the capture was taken with, not whatever is on disk
        // under the same name today. Say so, because it changes how a reading should be read.
        DefinitionOrigin = $"EMBEDDED IN RECORDING · {set.Channels.Count} CHANNELS";

        _recording = file;
        _engine = new ReplayEngine(SystemClock.Instance, SpeedByIndex[SpeedIndex]);

        HasRecording = true;
        RecordingName = Path.GetFileName(file.Path);
        DurationSeconds = Math.Max(0.001, file.DurationMicros / 1_000_000.0);

        // Show where the capture ended rather than an empty scope. Play then restarts from the
        // beginning, because the playhead is already past the last record.
        PreviewUpTo(file.DurationMicros);
        ReplayState = TransportState.Idle;

        RecordingSummary =
            $"{file.Records.Count:N0} frames · {FormatBytes(file.TotalFrameBytes)} · {DurationSeconds:0.0} s";

        Report(
            file.Truncated
                ? $"{RecordingName} was cut short; recovered {file.Records.Count:N0} complete records."
                : $"Loaded {RecordingName}: {file.Records.Count:N0} records.",
            error: file.Truncated);
    }

    private void StartReplay(int startIndex)
    {
        if (_recording is null || _engine is null || _pipeline is null)
        {
            return;
        }

        if (startIndex >= _recording.Records.Count)
        {
            // Playing from the end means playing again, so start over rather than doing nothing.
            startIndex = 0;
            _pipeline.Reset();
            _queue.Clear();
        }

        StopReplayTask();
        _engineResumeIndex = startIndex;

        var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        _engine.Speed = SpeedByIndex[SpeedIndex];

        var records = _recording.Records;
        var engine = _engine;
        var queue = _queue;

        ReplayState = TransportState.Running;
        Report("Replaying.", error: false);

        _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(
                    records,
                    (record, _) => queue.Enqueue(record.TimestampMicros, record.Frame),
                    startIndex,
                    cancellation.Token).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!cancellation.IsCancellationRequested)
                    {
                        ReplayState = TransportState.Paused;
                        Report("Replay reached the end of the recording.", error: false);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Pausing and seeking both cancel the run; neither is a failure.
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ReplayState = TransportState.Idle;
                    Report($"Replay failed: {ex.Message}", error: true);
                });
            }
        }, cancellation.Token);
    }

    private void SeekTo(long recordMicros)
    {
        if (_recording is null || _pipeline is null)
        {
            return;
        }

        var wasRunning = ReplayState == TransportState.Running;
        StopReplayTask();

        // Everything on screen belongs to the old position, so it goes.
        _pipeline.Reset();
        _queue.Clear();
        CursorMicros = null;

        PreviewUpTo(recordMicros);

        if (wasRunning)
        {
            StartReplay(_engineResumeIndex);
        }
        else
        {
            ReplayState = TransportState.Paused;
        }

        RefreshReadouts();
        RaiseChanged();
    }

    /// <summary>
    /// Fills the scope with the window of recording that precedes <paramref name="recordMicros"/>,
    /// without waiting for any of it.
    ///
    /// Scrubbing a capture should show the data leading up to where the handle is — that is what a
    /// scope displays when it is running, and a scrub that blanks the screen until playback
    /// resumes is unusable for finding the moment something went wrong.
    /// </summary>
    private void PreviewUpTo(long recordMicros)
    {
        if (_recording is null || _pipeline is null)
        {
            return;
        }

        _pipeline.Reset();
        _queue.Clear();

        var records = _recording.Records;
        var first = ReplayEngine.IndexAt(records, recordMicros - WindowMicros);
        var index = first;

        while (index < records.Count && records[index].TimestampMicros <= recordMicros)
        {
            _pipeline.Accept(records[index].TimestampMicros, records[index].Frame);
            index++;
        }

        // Playback resumes from the first record that has not been shown yet.
        _engine = new ReplayEngine(SystemClock.Instance, SpeedByIndex[SpeedIndex]);
        _engineResumeIndex = index;

        UpdatePosition(recordMicros / 1_000_000.0);
        RefreshReadouts();
        RaiseChanged();
    }

    private void StopReplayTask()
    {
        var cancellation = _replayCancellation;
        _replayCancellation = null;

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    // --- the beat

    private void Tick()
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        _batch.Clear();
        var taken = _queue.DrainTo(_batch, MaxFramesPerTick);

        foreach (var frame in _batch)
        {
            pipeline.Accept(frame.TimeMicros, frame.Bytes);
        }

        // A live scope repaints on every beat whether or not anything arrived, because the trace
        // scrolling away from a flat line is how a dead link looks. Everything else only repaints
        // when there is something new to show.
        if (taken == 0 && !(IsReceiving && !IsFrozen))
        {
            return;
        }

        if (_recording is not null && ReplayState == TransportState.Running)
        {
            UpdatePosition(pipeline.LastFrameMicros / 1_000_000.0);
        }

        RefreshReadouts();
        RaiseChanged();
    }

    private void RefreshReadouts()
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        var to = RightEdgeMicros;
        var from = to - WindowMicros;

        foreach (var channel in Channels)
        {
            channel.Refresh(from, to);
        }

        FrameCountText = pipeline.FrameCount.ToString("N0", CultureInfo.InvariantCulture);
        ByteCountText = FormatBytes(pipeline.ByteCount);
        ViolationText = pipeline.ViolationFrameCount.ToString("N0", CultureInfo.InvariantCulture);
        DroppedText = _queue.Dropped.ToString("N0", CultureInfo.InvariantCulture);
        DiscardedText = FormatBytes(_source?.DiscardedBytes ?? 0);

        var elapsedSeconds = to / 1_000_000.0;
        RateText = elapsedSeconds > 0.05
            ? $"{pipeline.FrameCount / elapsedSeconds:N0} /s"
            : "-- /s";
    }

    private void UpdatePosition(double seconds)
    {
        _updatingPosition = true;
        PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _updatingPosition = false;
    }

    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the properties the chart draws from are worth a repaint; the read-out strings
        // change on every beat and are already handled by the beat itself.
        if (e.PropertyName is nameof(ChannelViewModel.IsTraceVisible))
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void Report(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
    }

    /// <summary>
    /// Applies command-line startup: load what was asked for and, if requested, start receiving.
    /// Failures are reported in the status bar rather than thrown, because a window that has
    /// already opened should stay open and say what went wrong.
    /// </summary>
    public async Task ApplyStartupAsync(StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            if (options.RecordingPath is { } recording)
            {
                LoadRecording(TsrReader.ReadFile(recording));
            }
            else if (options.DefinitionPath is { } definition)
            {
                LoadDefinition(ChannelSetReader.ReadFile(definition), definition);
            }
        }
        catch (Exception ex) when (ex is DefinitionException or TsrFormatException or IOException)
        {
            Report(ex.Message, error: true);
            return;
        }

        if (options.UdpPort is { } port)
        {
            SourceKindIndex = 0;
            UdpPort = port.ToString(CultureInfo.InvariantCulture);
        }

        if (options.SerialPort is { } serialPort)
        {
            SourceKindIndex = 1;
            RefreshSerialPorts();
            SelectedSerialPort = serialPort;
        }

        if (options.Connect && HasDefinition)
        {
            await ConnectAsync().ConfigureAwait(true);
        }
    }

    internal static string Describe(FramingDef framing) => framing.Mode switch
    {
        FramingMode.Fixed => $"FIXED {framing.FrameLength} B",
        FramingMode.LengthField =>
            $"LENGTH @{framing.LengthOffset} · {framing.LengthSize} B · {(framing.Adjust >= 0 ? "+" : "-")}{Math.Abs(framing.Adjust)}",
        FramingMode.Delimiter => $"DELIMITER {Convert.ToHexString(framing.Delimiter)}",
        _ => "--",
    };

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };
}
