#nullable enable

using SplitGM.Core;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SplitGM.Gui;

public partial class ReconstructionProgressWindow : Window
{
    // UMT keeps heavy work off the UI thread and lets a small updater poll shared
    // progress state at roughly 30 Hz. SplitGM uses the same model here: worker
    // threads only enqueue/coalesce state, while this timer performs bounded UI work.
    private const int UiPumpIntervalMilliseconds = 33;
    private const int CatalogBatchSize = 4096;
    private const int ResourceUpdatesPerTick = 512;
    private const int FinishedResourceUpdatesPerTick = 4096;
    private const int LogMessagesPerTick = 256;
    private const int AutoScrollIntervalMilliseconds = 350;
    private const int PreviewIntervalMilliseconds = 250;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer _uiPump;
    private readonly BulkObservableCollection<ReconstructionResourceRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReconstructionProgress> _pendingResourceUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<LogMessage> _pendingLogs = new();

    private ReconstructionProgress? _latestProgress;
    private ReconstructionProgress? _latestResourceProgress;
    private ReconstructionProgress? _latestPreviewProgress;
    private IReadOnlyList<ReconstructionResourceCatalogItem>? _incomingCatalog;
    private IReadOnlyList<ReconstructionResourceCatalogItem>? _catalog;
    private int _catalogLoadIndex;
    private bool _operationFinished;
    private bool _allowClose;
    private bool _catalogLogWritten;
    private ReconstructionStage? _lastStage;
    private long _lastElapsedUiTimestamp;
    private long _lastScrollUiTimestamp;
    private long _lastPreviewUiTimestamp;

    public event EventHandler? CancelRequested;
    public event Action<ReconstructionProgress>? ProgressDisplayed;
    public event Action<IReadOnlyList<LogMessage>>? LogsDisplayed;

    public ReconstructionProgressWindow(string description, string outputPath)
    {
        InitializeComponent();
        DescriptionText.Text = description;
        OutputPathText.Text = outputPath;
        StageText.Text = "Preparing";
        ProgressMessageText.Text = "Preparing reconstructed project export...";
        CurrentResourceText.Text = "No resource selected yet";
        CurrentOutputText.Text = string.Empty;
        ElapsedText.Text = "00:00:00";
        ResourceGrid.ItemsSource = _rows;

        _uiPump = new DispatcherTimer(
            TimeSpan.FromMilliseconds(UiPumpIntervalMilliseconds),
            DispatcherPriority.Normal,
            (_, _) => PumpUi(),
            Dispatcher);
        _uiPump.Start();

        AppendRaw("START", "Reconstructed .yyp project export started.");
        AppendRaw("OUTPUT", outputPath);
    }

    /// <summary>
    /// Thread-safe progress entry point. Unlike Progress&lt;T&gt;, this does not post one
    /// Dispatcher operation per resource. Repeated updates are coalesced by resource.
    /// </summary>
    public void EnqueueProgress(ReconstructionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Interlocked.Exchange(ref _latestProgress, progress);

        if (progress.ResourceCatalog is { Count: > 0 })
            Interlocked.CompareExchange(ref _incomingCatalog, progress.ResourceCatalog, null);

        if (!string.IsNullOrWhiteSpace(progress.ResourceName))
        {
            string key = BuildResourceKey(progress);
            _pendingResourceUpdates.AddOrUpdate(key, progress, (_, _) => progress);
            Interlocked.Exchange(ref _latestResourceProgress, progress);
            if (progress.PreviewPng is { Length: > 0 } || !string.IsNullOrWhiteSpace(progress.PreviewText))
                Interlocked.Exchange(ref _latestPreviewProgress, progress);
        }
    }

    public void EnqueueLog(LogMessage message) => _pendingLogs.Enqueue(message);

    // Kept for callers compiled against the earlier progress-window helper.
    public void UpdateProgress(ReconstructionProgress progress) => EnqueueProgress(progress);
    public void AppendLog(LogMessage message) => EnqueueLog(message);

    public void Complete(bool success, string summary, bool autoClose)
    {
        // Apply the newest aggregate state before showing the final result. The
        // remaining resource-row backlog continues draining without blocking close.
        PumpUi();

        _operationFinished = true;
        _stopwatch.Stop();
        if (success)
            ExportProgressBar.Value = 100;
        StageText.Text = success ? "Completed" : "Stopped";
        ProgressMessageText.Text = summary;
        CompletionText.Text = summary;
        CancelExportButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        AppendRaw(success ? "SUCCESS" : "STOPPED", summary);

        if (autoClose)
        {
            _allowClose = true;
            Close();
        }
    }

    private void PumpUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(PumpUi));
            return;
        }

        UpdateElapsed();
        AdoptIncomingCatalog();
        LoadCatalogBatch();
        ApplyLatestAggregateProgress();
        ApplyResourceUpdateBatch();
        ApplyCurrentResourceDisplay();
        ApplyPreviewDisplay();
        FlushLogBatch();

        if (_operationFinished && IsDrainComplete())
            _uiPump.Stop();
    }

    private void UpdateElapsed()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastElapsedUiTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastElapsedUiTimestamp, now) < TimeSpan.FromMilliseconds(200))
        {
            return;
        }

        _lastElapsedUiTimestamp = now;
        ElapsedText.Text = FormatElapsed(_stopwatch.Elapsed);
    }

    private void AdoptIncomingCatalog()
    {
        if (_catalog is not null)
            return;

        IReadOnlyList<ReconstructionResourceCatalogItem>? incoming = Interlocked.Exchange(ref _incomingCatalog, null);
        if (incoming is null)
            return;

        _catalog = incoming;
        _catalogLoadIndex = 0;
    }

    private void LoadCatalogBatch()
    {
        if (_catalog is null || _catalogLoadIndex >= _catalog.Count)
            return;

        int end = Math.Min(_catalog.Count, _catalogLoadIndex + CatalogBatchSize);
        List<ReconstructionResourceRow> batch = new(end - _catalogLoadIndex);
        for (int i = _catalogLoadIndex; i < end; i++)
        {
            ReconstructionResourceCatalogItem item = _catalog[i];
            string type = item.ResourceKind?.ToString() ?? "Script";
            string key = BuildResourceKey(item);
            if (_rowIndexes.ContainsKey(key))
                continue;

            int rowIndex = _rows.Count + batch.Count;
            _rowIndexes[key] = rowIndex;
            batch.Add(new ReconstructionResourceRow("Queued", type, item.ResourceName, item.RelativeOutputPath));
        }

        _catalogLoadIndex = end;
        if (batch.Count > 0)
            _rows.AddRange(batch);

        ResourceCountText.Text = _catalogLoadIndex < _catalog.Count
            ? $"{_rows.Count:N0} / {_catalog.Count:N0} loaded"
            : $"{_rows.Count:N0} listed";

        if (_catalogLoadIndex >= _catalog.Count && !_catalogLogWritten)
        {
            _catalogLogWritten = true;
            AppendRaw("QUEUE", $"Loaded {_rows.Count:N0} scripts and resources into the export list.");
        }
    }

    private void ApplyLatestAggregateProgress()
    {
        ReconstructionProgress? progress = Interlocked.Exchange(ref _latestProgress, null);
        if (progress is null)
            return;

        if (_lastStage != progress.Stage)
        {
            _lastStage = progress.Stage;
            AppendRaw("STAGE", $"{FriendlyStage(progress.Stage)} — {progress.Message}");
        }

        StageText.Text = FriendlyStage(progress.Stage);
        ProgressMessageText.Text = progress.Message;
        ExportProgressBar.Value = progress.Total > 0 ? progress.Percentage : 0;
        ProgressCountText.Text = progress.Total > 0
            ? $"{Math.Min(progress.Completed, progress.Total):N0} / {progress.Total:N0}  ({progress.Percentage:0.0}%)"
            : string.Empty;

        ProgressDisplayed?.Invoke(progress);
    }

    private void ApplyResourceUpdateBatch()
    {
        int budget = _operationFinished ? FinishedResourceUpdatesPerTick : ResourceUpdatesPerTick;
        int visited = 0;

        foreach (KeyValuePair<string, ReconstructionProgress> pair in _pendingResourceUpdates)
        {
            if (visited++ >= budget)
                break;
            if (!_pendingResourceUpdates.TryRemove(pair.Key, out ReconstructionProgress? progress))
                continue;

            if (!_rowIndexes.TryGetValue(pair.Key, out int rowIndex) || rowIndex < 0 || rowIndex >= _rows.Count)
            {
                // Catalog rows are loaded in bounded batches. Keep the newest state
                // until its row exists rather than losing early worker updates.
                _pendingResourceUpdates.AddOrUpdate(pair.Key, progress, (_, existing) =>
                    existing.Completed >= progress.Completed ? existing : progress);
                continue;
            }

            ReconstructionResourceRow row = _rows[rowIndex];
            row.Status = progress.Status ?? "Working";
            row.Type = progress.ResourceKind?.ToString() ?? "Script";
            row.Name = progress.ResourceName ?? row.Name;
            row.Output = progress.RelativeOutputPath ?? row.Output;
        }

        if (_catalog is not null)
        {
            ResourceCountText.Text = _catalogLoadIndex < _catalog.Count
                ? $"{_rows.Count:N0} / {_catalog.Count:N0} loaded"
                : $"{_rows.Count:N0} listed";
        }
    }

    private void ApplyCurrentResourceDisplay()
    {
        ReconstructionProgress? progress = Interlocked.Exchange(ref _latestResourceProgress, null);
        if (progress is null || string.IsNullOrWhiteSpace(progress.ResourceName))
            return;

        string type = progress.ResourceKind?.ToString() ?? "Script";
        CurrentResourceText.Text = $"{type}: {progress.ResourceName}  •  {progress.Status ?? "Working"}";
        CurrentOutputText.Text = progress.RelativeOutputPath ?? string.Empty;

        long now = Stopwatch.GetTimestamp();
        if (_lastScrollUiTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastScrollUiTimestamp, now) < TimeSpan.FromMilliseconds(AutoScrollIntervalMilliseconds))
        {
            return;
        }

        _lastScrollUiTimestamp = now;
        string key = BuildResourceKey(progress);
        if (!_rowIndexes.TryGetValue(key, out int rowIndex) || rowIndex < 0 || rowIndex >= _rows.Count)
            return;

        ResourceGrid.SelectedIndex = rowIndex;
        ResourceGrid.ScrollIntoView(_rows[rowIndex]);
    }

    private void ApplyPreviewDisplay()
    {
        ReconstructionProgress? progress = Volatile.Read(ref _latestPreviewProgress);
        if (progress is null)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_lastPreviewUiTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastPreviewUiTimestamp, now) < TimeSpan.FromMilliseconds(PreviewIntervalMilliseconds))
        {
            return;
        }

        // Clear only after claiming this sample, so a newer preview cannot be lost
        // while the UI is decoding the current one.
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _latestPreviewProgress, null, progress), progress))
            return;

        _lastPreviewUiTimestamp = now;
        if (progress.PreviewPng is { Length: > 0 })
        {
            ResourcePreviewImage.Source = BitmapSourceFactory.FromBytes(progress.PreviewPng);
            ResourcePreviewImage.Visibility = Visibility.Visible;
            ResourcePreviewTextBox.Visibility = Visibility.Collapsed;
            NoPreviewText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.IsNullOrWhiteSpace(progress.PreviewText))
        {
            ResourcePreviewImage.Source = null;
            ResourcePreviewImage.Visibility = Visibility.Collapsed;
            ResourcePreviewTextBox.Text = progress.PreviewText;
            ResourcePreviewTextBox.ScrollToHome();
            ResourcePreviewTextBox.Visibility = Visibility.Visible;
            NoPreviewText.Visibility = Visibility.Collapsed;
        }
    }

    private void FlushLogBatch()
    {
        List<LogMessage> messages = new(LogMessagesPerTick);
        while (messages.Count < LogMessagesPerTick && _pendingLogs.TryDequeue(out LogMessage? message))
            messages.Add(message);

        if (messages.Count == 0)
            return;

        StringBuilder output = new();
        foreach (LogMessage message in messages)
            output.Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss")).Append("] ")
                .Append(message.Level.ToString().ToUpperInvariant().PadRight(8)).Append(' ')
                .AppendLine(message.Text);

        ExportLogTextBox.AppendText(output.ToString());
        ExportLogTextBox.ScrollToEnd();
        LogsDisplayed?.Invoke(messages);
    }

    private bool IsDrainComplete() =>
        Volatile.Read(ref _latestProgress) is null &&
        Volatile.Read(ref _latestResourceProgress) is null &&
        Volatile.Read(ref _latestPreviewProgress) is null &&
        _pendingResourceUpdates.IsEmpty &&
        _pendingLogs.IsEmpty &&
        (_catalog is null || _catalogLoadIndex >= _catalog.Count);

    private void AppendRaw(string level, string text)
    {
        ExportLogTextBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {level,-8} {text}{Environment.NewLine}");
        ExportLogTextBox.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationFinished)
            return;
        CancelExportButton.IsEnabled = false;
        ProgressMessageText.Text = "Cancellation requested. Waiting for the current safe stopping point...";
        AppendRaw("CANCEL", "Cancellation requested by the user.");
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationFinished || _allowClose)
            return;
        e.Cancel = true;
        Cancel_Click(this, new RoutedEventArgs());
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiPump.Stop();
        base.OnClosed(e);
    }

    private static string BuildResourceKey(ReconstructionProgress progress) =>
        progress.ResourceKind is ResourceKind kind && progress.ResourceIndex >= 0
            ? $"{kind}:{progress.ResourceIndex}"
            : $"Script:{progress.ResourceName}";

    private static string BuildResourceKey(ReconstructionResourceCatalogItem item) =>
        item.ResourceKind is ResourceKind kind && item.ResourceIndex >= 0
            ? $"{kind}:{item.ResourceIndex}"
            : $"Script:{item.ResourceName}";

    private static string FriendlyStage(ReconstructionStage stage) => stage switch
    {
        ReconstructionStage.Preparing => "Preparing output",
        ReconstructionStage.SelectingTargetProfile => "Selecting GameMaker target",
        ReconstructionStage.DecompilingCode => "Decompiling VM code",
        ReconstructionStage.BuildingIntermediateProject => "Writing .splitgmproj",
        ReconstructionStage.ExportingResources => "Exporting reconstructed resources",
        ReconstructionStage.WritingGameMakerProject => "Writing .yyp project",
        ReconstructionStage.RepairingProject => "Automatic project repair",
        ReconstructionStage.CompilePreflight => "Static compile preflight",
        ReconstructionStage.ValidatingProject => "Validating reconstruction",
        ReconstructionStage.Completed => "Completed",
        _ => stage.ToString()
    };

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}

public sealed class ReconstructionResourceRow : INotifyPropertyChanged
{
    private string _status;
    private string _type;
    private string _name;
    private string _output;

    public ReconstructionResourceRow(string status, string type, string name, string output) =>
        (_status, _type, _name, _output) = (status, type, name, output);

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value, nameof(Status));
    }

    public string Type
    {
        get => _type;
        set => SetField(ref _type, value, nameof(Type));
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value, nameof(Name));
    }

    public string Output
    {
        get => _output;
        set => SetField(ref _output, value, nameof(Output));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        foreach (T item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
