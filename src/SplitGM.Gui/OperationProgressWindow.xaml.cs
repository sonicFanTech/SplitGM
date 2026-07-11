#nullable enable

using SplitGM.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace SplitGM.Gui;

public partial class OperationProgressWindow : Window
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private bool _operationFinished;
    private bool _allowClose;
    private DecompileStage? _lastStage;

    public event EventHandler? CancelRequested;

    public OperationProgressWindow(string title, string description, string path)
    {
        InitializeComponent();
        OperationTitleText.Text = title;
        OperationDescriptionText.Text = description;
        PathText.Text = path;
        StageText.Text = "Starting";
        CurrentItemText.Text = "Preparing operation...";
        ProgressMessageText.Text = "Preparing...";
        ElapsedText.Text = "00:00:00";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            ElapsedText.Text = FormatElapsed(_stopwatch.Elapsed);
        }, Dispatcher);
        _timer.Start();
        OperationLogTextBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] START   {title}{Environment.NewLine}");
        if (!string.IsNullOrWhiteSpace(path))
            OperationLogTextBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] PATH    {path}{Environment.NewLine}");
    }

    public void UpdateProgress(DecompileProgress progress)
    {
        if (_lastStage != progress.Stage)
        {
            _lastStage = progress.Stage;
            OperationLogTextBox.AppendText(
                $"[{DateTimeOffset.Now:HH:mm:ss}] STAGE   {FriendlyStage(progress.Stage)} — {progress.Message}{Environment.NewLine}");
            OperationLogTextBox.ScrollToEnd();
        }
        StageText.Text = FriendlyStage(progress.Stage);
        CurrentItemText.Text = progress.Message;
        ProgressMessageText.Text = progress.Message;
        OperationProgressBar.Value = progress.Total > 0 ? progress.Percentage : 0;
        ProgressCountText.Text = progress.Total > 0
            ? $"{Math.Min(progress.Completed, progress.Total):N0} / {progress.Total:N0}  ({progress.Percentage:0.0}%)"
            : string.Empty;
    }

    public void AppendLog(LogMessage message)
    {
        OperationLogTextBox.AppendText($"[{message.Timestamp:HH:mm:ss}] {message.Level.ToString().ToUpperInvariant(),-7} {message.Text}{Environment.NewLine}");
        OperationLogTextBox.ScrollToEnd();
    }

    public void Complete(bool success, string summary, bool autoClose)
    {
        _operationFinished = true;
        _stopwatch.Stop();
        _timer.Stop();
        OperationProgressBar.Value = success ? 100 : OperationProgressBar.Value;
        StageText.Text = success ? "Completed" : "Stopped";
        CompletionText.Text = summary;
        ProgressMessageText.Text = summary;
        CancelOperationButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        if (autoClose)
        {
            _allowClose = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelOperationButton.IsEnabled = false;
        ProgressMessageText.Text = "Cancellation requested. Waiting for the current safe stopping point...";
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

    private static string FriendlyStage(DecompileStage stage) => stage switch
    {
        DecompileStage.ResolvingInput => "Resolving input",
        DecompileStage.LoadingData => "Loading GameMaker data",
        DecompileStage.InspectingGame => "Inspecting game",
        DecompileStage.BuildingResourceIndex => "Building resource index",
        DecompileStage.DecompilingCode => "Decompiling VM code",
        DecompileStage.ExportingAssembly => "Exporting VM assembly",
        DecompileStage.WritingManifest => "Writing project manifest",
        DecompileStage.ExportingResources => "Exporting resources",
        DecompileStage.RenderingPreview => "Rendering preview",
        DecompileStage.SearchingCode => "Searching and analyzing code",
        DecompileStage.Completed => "Completed",
        _ => stage.ToString()
    };
    private static string FormatElapsed(TimeSpan elapsed) => $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
