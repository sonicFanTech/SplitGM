#nullable enable

using SplitGM.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace SplitGM.Gui;

public partial class ReconstructionProgressWindow : Window
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly BulkObservableCollection<ReconstructionResourceRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndexes = new(StringComparer.OrdinalIgnoreCase);
    private bool _operationFinished;
    private bool _allowClose;
    private ReconstructionStage? _lastStage;

    public event EventHandler? CancelRequested;

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

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            ElapsedText.Text = FormatElapsed(_stopwatch.Elapsed);
        }, Dispatcher);
        _timer.Start();

        AppendRaw("START", "Reconstructed .yyp project export started.");
        AppendRaw("OUTPUT", outputPath);
    }

    public void UpdateProgress(ReconstructionProgress progress)
    {
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

        if (progress.ResourceCatalog is { Count: > 0 })
            LoadResourceCatalog(progress.ResourceCatalog);

        if (!string.IsNullOrWhiteSpace(progress.ResourceName))
        {
            string type = progress.ResourceKind?.ToString() ?? "Script";
            string key = progress.ResourceKind is ResourceKind kind && progress.ResourceIndex >= 0
                ? $"{kind}:{progress.ResourceIndex}"
                : $"Script:{progress.ResourceName}";
            ReconstructionResourceRow row = new(
                progress.Status ?? "Working",
                type,
                progress.ResourceName,
                progress.RelativeOutputPath ?? string.Empty);

            int currentIndex;
            if (_rowIndexes.TryGetValue(key, out int existingIndex))
            {
                currentIndex = existingIndex;
                _rows[existingIndex] = row;
            }
            else
            {
                currentIndex = _rows.Count;
                _rowIndexes[key] = currentIndex;
                _rows.Add(row);
            }

            ResourceGrid.SelectedIndex = currentIndex;
            ResourceGrid.ScrollIntoView(_rows[currentIndex]);
            ResourceCountText.Text = $"{_rows.Count:N0} listed";
            CurrentResourceText.Text = $"{type}: {progress.ResourceName}  •  {progress.Status ?? "Working"}";
            CurrentOutputText.Text = progress.RelativeOutputPath ?? string.Empty;

            if (progress.PreviewPng is { Length: > 0 })
            {
                ResourcePreviewImage.Source = BitmapSourceFactory.FromBytes(progress.PreviewPng);
                ResourcePreviewImage.Visibility = Visibility.Visible;
                ResourcePreviewTextBox.Visibility = Visibility.Collapsed;
                NoPreviewText.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrWhiteSpace(progress.PreviewText))
            {
                ResourcePreviewImage.Source = null;
                ResourcePreviewImage.Visibility = Visibility.Collapsed;
                ResourcePreviewTextBox.Text = progress.PreviewText;
                ResourcePreviewTextBox.ScrollToHome();
                ResourcePreviewTextBox.Visibility = Visibility.Visible;
                NoPreviewText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ResourcePreviewImage.Source = null;
                ResourcePreviewImage.Visibility = Visibility.Collapsed;
                ResourcePreviewTextBox.Clear();
                ResourcePreviewTextBox.Visibility = Visibility.Collapsed;
                NoPreviewText.Text = progress.ResourceKind is ResourceKind.Sounds or ResourceKind.EmbeddedAudio or ResourceKind.AudioGroups
                    ? "Audio is being exported, but audio resources do not have a visual preview in SplitGM yet."
                    : "This resource does not currently have a displayable visual or text preview.";
                NoPreviewText.Visibility = Visibility.Visible;
            }
        }
    }

    private void LoadResourceCatalog(IReadOnlyList<ReconstructionResourceCatalogItem> catalog)
    {
        if (_rows.Count > 0)
            return;

        List<ReconstructionResourceRow> rows = new(catalog.Count);
        foreach (ReconstructionResourceCatalogItem item in catalog)
        {
            string type = item.ResourceKind?.ToString() ?? "Script";
            string key = item.ResourceKind is ResourceKind kind && item.ResourceIndex >= 0
                ? $"{kind}:{item.ResourceIndex}"
                : $"Script:{item.ResourceName}";
            if (_rowIndexes.ContainsKey(key))
                continue;
            _rowIndexes[key] = rows.Count;
            rows.Add(new ReconstructionResourceRow("Queued", type, item.ResourceName, item.RelativeOutputPath));
        }

        _rows.AddRange(rows);
        ResourceCountText.Text = $"{_rows.Count:N0} listed";
        AppendRaw("QUEUE", $"Loaded {_rows.Count:N0} scripts and resources into the export list.");
    }

    public void AppendLog(LogMessage message) =>
        AppendRaw(message.Level.ToString().ToUpperInvariant(), message.Text);

    public void Complete(bool success, string summary, bool autoClose)
    {
        _operationFinished = true;
        _stopwatch.Stop();
        _timer.Stop();
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_operationFinished || _allowClose)
            return;
        e.Cancel = true;
        Cancel_Click(this, new RoutedEventArgs());
    }

    private static string FriendlyStage(ReconstructionStage stage) => stage switch
    {
        ReconstructionStage.Preparing => "Preparing output",
        ReconstructionStage.SelectingTargetProfile => "Selecting GameMaker target",
        ReconstructionStage.DecompilingCode => "Decompiling VM code",
        ReconstructionStage.BuildingIntermediateProject => "Writing .splitgmproj",
        ReconstructionStage.ExportingResources => "Exporting reconstructed resources",
        ReconstructionStage.WritingGameMakerProject => "Writing .yyp project",
        ReconstructionStage.ValidatingProject => "Validating reconstruction",
        ReconstructionStage.Completed => "Completed",
        _ => stage.ToString()
    };

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}

public sealed record ReconstructionResourceRow(string Status, string Type, string Name, string Output);

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
