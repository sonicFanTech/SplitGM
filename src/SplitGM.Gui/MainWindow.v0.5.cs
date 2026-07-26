#nullable enable

using Microsoft.Win32;
using SplitGM.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SplitGM.Gui;

public partial class MainWindow
{
    private ReconstructionProgressWindow? _reconstructionWindow;

    private void InitializeV05()
    {
        RelationshipSummaryTextBox.Text =
            "Select a code entry or resource, then choose Tools > Analyze selected relationships.\r\n\r\n" +
            "SplitGM v0.5.1.0 adds automatic report-first repair, direct UMT batch decompilation, " +
            "real audio waveforms, and an organized read-only room viewer.";
    }

    private void StartReconstructionWindow(string outputPath)
    {
        CloseReconstructionWindow(force: true);
        ReconstructionProgressWindow window = new(
            "SplitGM will decompile VM code, reconstruct editable project resources, preserve relationships, and report anything that cannot be represented safely.",
            outputPath)
        {
            Owner = this
        };
        _reconstructionWindow = window;
        window.CancelRequested += (_, _) => _operationCancellation?.Cancel();
        window.ProgressDisplayed += update =>
        {
            StatusTextBlock.Text = update.Message;
            ProgressBar.Value = update.Total > 0 ? update.Percentage : 0;
        };
        window.LogsDisplayed += messages =>
        {
            foreach (LogMessage message in messages)
                AppendLog(message);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_reconstructionWindow, window))
                _reconstructionWindow = null;
        };
        window.Show();
    }

    private IProgress<ReconstructionProgress> CreateReconstructionProgress() =>
        new DirectProgress<ReconstructionProgress>(update =>
            _reconstructionWindow?.EnqueueProgress(update));

    private IProgress<LogMessage> CreateReconstructionLog() =>
        new DirectProgress<LogMessage>(message =>
            _reconstructionWindow?.EnqueueLog(message));

    private void CompleteReconstructionWindow(bool success, string summary)
    {
        if (_reconstructionWindow is null)
            return;
        _reconstructionWindow.Complete(success, summary, success && _settings.AutoCloseOperationWindow);
        if (success && _settings.AutoCloseOperationWindow)
            _reconstructionWindow = null;
    }

    private void CloseReconstructionWindow(bool force)
    {
        if (_reconstructionWindow is null || !force)
            return;
        _reconstructionWindow.Complete(false, "Reconstructed-project operation window closed.", autoClose: true);
        _reconstructionWindow = null;
    }

    private async void DecompileReconstructedYypButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _isBusy || !_settings.EnableReconstructedYypExport)
            return;

        if (!_settings.ExperimentalYypWarningAccepted)
        {
            MessageBoxResult warning = MessageBox.Show(this,
                "Decompile to .yyp Project is experimental.\n\nGenerated projects are transparent repair workspaces and may still require manual fixes, GameMaker IDE validation, or compile repairs before they can run.\n\nContinue?",
                "Experimental reconstructed .yyp export",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (warning != MessageBoxResult.OK)
                return;

            _settings.ExperimentalYypWarningAccepted = true;
            _settings.Save();
        }

        string initialDirectory = !string.IsNullOrWhiteSpace(_settings.DefaultExportDirectory) &&
                                  Directory.Exists(_settings.DefaultExportDirectory)
            ? _settings.DefaultExportDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        OpenFolderDialog dialog = new()
        {
            Title = "Select the parent folder for the reconstructed .yyp project",
            InitialDirectory = initialDirectory
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string safeGameName = OutputPathHelper.SafeFileName(_session.Info.DisplayName);
        string outputDirectory = Path.Combine(dialog.FolderName, safeGameName + "_Reconstructed");
        bool overwrite = Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any();
        if (overwrite)
        {
            bool isSplitGmOutput = File.Exists(Path.Combine(outputDirectory, ".splitgm-reconstructed-project")) ||
                                   Directory.EnumerateFiles(outputDirectory, "*.splitgmproj", SearchOption.TopDirectoryOnly).Any();
            if (!isSplitGmOutput)
            {
                MessageBox.Show(this,
                    $"SplitGM will not replace this non-empty folder because it was not created by the reconstructed-project exporter:\n\n{outputDirectory}\n\nChoose another parent folder, rename the existing folder, or remove it manually after checking its contents.",
                    "Unsafe reconstruction target",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_settings.ConfirmOverwrite)
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    $"The previous reconstructed project will be replaced:\n\n{outputDirectory}\n\nContinue?",
                    "Replace reconstructed project",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return;
            }
        }

        SetBusy(true, "Building reconstructed .yyp project...");
        _operationCancellation = new CancellationTokenSource();
        StartReconstructionWindow(outputDirectory);
        MainTabControl.SelectedItem = ActivityTab;
        IProgress<ReconstructionProgress> progress = CreateReconstructionProgress();
        IProgress<LogMessage> log = CreateReconstructionLog();

        try
        {
            CancelCurrentPreview();
            GameProjectSession session = _session ?? throw new InvalidOperationException("The loaded GameMaker session was closed before reconstruction started.");
            await session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            ReconstructedProjectOptions exportOptions = new(
                outputDirectory,
                overwrite,
                ExportRawFallbacks: true,
                ExportAssemblyFallbacks: _settings.ExportAssembly,
                ValidateOutput: true,
                RunAutomaticRepair: true,
                GameProfile: GetEffectiveGameProfile());

            // Match UMT's long-operation pattern: move the entire synchronous prefix
            // and async export pipeline onto a worker task. Progress is polled and
            // coalesced by ReconstructionProgressWindow instead of flooding Dispatcher.
            ReconstructedProjectResult result = await Task.Run(
                () => session.ExportReconstructedProjectAsync(
                    exportOptions,
                    progress,
                    log,
                    _operationCancellation.Token),
                _operationCancellation.Token);

            _lastOutputDirectory = result.OutputDirectory;
            OpenOutputButton.IsEnabled = true;
            ProgressBar.Value = 100;
            StatusTextBlock.Text = "Reconstructed .yyp project completed.";
            StatusDetailTextBlock.Text =
                $"{result.ResourcesRepresented:N0} represented • {result.ResourcesPreservedAsFallback:N0} fallback • " +
                $"{result.RepairsApplied:N0} repairs • {result.ManualRepairItems:N0} manual • " +
                $"{result.WarningCount:N0} warnings • {result.ErrorCount:N0} errors";

            bool cleanCompletion = result.ErrorCount == 0 && result.CompilePreflightPassed;
            string summary = cleanCompletion
                ? $"Reconstructed project completed: {result.ResourcesRepresented:N0} resources represented, {result.RepairsApplied:N0} automatic repairs applied, and static compile preflight passed."
                : $"Reconstructed project completed with {result.ErrorCount:N0} export/validation error(s) and {result.ManualRepairItems:N0} manual-repair item(s). Review the repair and validation reports before opening it in GameMaker.";
            CompleteReconstructionWindow(cleanCompletion, summary);

            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputDirectory))
                Process.Start(new ProcessStartInfo(result.OutputDirectory) { UseShellExecute = true });

            MessageBox.Show(this,
                $"The reconstructed GameMaker project has been written.\n\n" +
                $"Project: {Path.GetFileName(result.ProjectFile)}\n" +
                $"Target: {result.TargetProfile}\n" +
                $"Represented in .yyp: {result.ResourcesRepresented:N0}\n" +
                $"Fallback-only resources: {result.ResourcesPreservedAsFallback:N0}\n" +
                $"Automatic repairs: {result.RepairsApplied:N0}\n" +
                $"Manual-review items: {result.ManualRepairItems:N0}\n" +
                $"Static compile preflight: {(result.CompilePreflightPassed ? "Passed" : "Needs review")}\n" +
                $"Warnings: {result.WarningCount:N0}\n" +
                $"Errors: {result.ErrorCount:N0}\n\n" +
                "This is a transparent repair workspace, not an identical copy of the original project. Read SplitGM-Repair-Report.txt and SplitGM-Reconstruction-Validation.txt before trying to compile it.",
                "Reconstructed .yyp export",
                MessageBoxButton.OK,
                cleanCompletion ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogMessage.Warning("Reconstructed .yyp export was cancelled."));
            StatusTextBlock.Text = "Reconstructed project export cancelled.";
            CompleteReconstructionWindow(false, "Reconstructed project export cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            StatusTextBlock.Text = "Reconstructed project export failed.";
            CompleteReconstructionWindow(false, "Reconstructed project export failed. See the log for details.");
            MessageBox.Show(this, exception.Message, "Reconstructed .yyp export error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void RunGameFromTempMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            MessageBox.Show(this,
                "Load a GameMaker game before using Run Game from TEMP.",
                "Run Game from TEMP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_isBusy || _isTempRunPreparing)
            return;

        DetectedGameProfile profile = GetEffectiveGameProfile();
        IReadOnlyList<TempGameRunnerCandidate> candidates = _session.DiscoverTempRunRunners(profile);
        string? runnerPath = SelectRunnerForTempRun(candidates);
        if (string.IsNullOrWhiteSpace(runnerPath))
            return;

        _isTempRunPreparing = true;
        RunGameFromTempMenuItem.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = "Preparing TEMP game run...";
        StatusDetailTextBlock.Text = $"Profile: {profile.DisplayName}";
        ProgressBar.Value = 0;
        _operationCancellation = new CancellationTokenSource();
        MainTabControl.SelectedItem = ActivityTab;
        GameProjectSession session = _session;
        Progress<TempRunProgress> progress = new(update =>
        {
            StatusTextBlock.Text = update.Message;
            if (update.BytesTotal > 0)
            {
                ProgressBar.Value = update.Percentage;
                StatusDetailTextBlock.Text =
                    $"{update.BytesCompleted:N0} / {update.BytesTotal:N0} bytes | {profile.DisplayName}";
            }
            else
            {
                StatusDetailTextBlock.Text = $"Profile: {profile.DisplayName} | {update.Stage}";
            }
        });

        try
        {
            AppendLog(LogMessage.Info("TEMP run requested from GUI."));
            TempGameRunResult result = await session.RunGameFromTempAsync(
                profile,
                runnerPath,
                progress,
                CreateDetailedLog(),
                _operationCancellation.Token);
            _currentTempRunDirectory = result.RunDirectory;
            OpenCurrentTempRunFolderMenuItem.IsEnabled = true;
            StatusTextBlock.Text = "Game launched from TEMP.";
            StatusDetailTextBlock.Text =
                $"Process {result.ProcessId:N0} | {result.CopiedSidecars.Count:N0} sidecar file(s) | {result.LaunchStrategy}";
            AppendLog(LogMessage.Success($"TEMP run started from {result.RunDirectory}"));
            AppendLog(LogMessage.Info($"TEMP manifest: {result.ManifestFilePath}"));
            MessageBox.Show(this,
                $"Started the game from a TEMP copy.\n\nTEMP folder: {result.RunDirectory}\nRunner: {result.RunnerExecutablePath}\nProcess ID: {result.ProcessId}\nSidecars copied: {result.CopiedSidecars.Count:N0}\n\nLaunch log: {result.LogFilePath}",
                "Run Game from TEMP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "TEMP game run cancelled.";
            AppendLog(LogMessage.Warning("TEMP game run preparation was cancelled."));
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            StatusTextBlock.Text = "TEMP game run failed.";
            if (exception is TempGameRunException tempException)
            {
                _currentTempRunDirectory = tempException.RunDirectory;
                OpenCurrentTempRunFolderMenuItem.IsEnabled = Directory.Exists(_currentTempRunDirectory);
            }

            string detail = exception is TempGameRunException { LogFilePath: { Length: > 0 } logFilePath }
                ? $"\n\nTEMP log: {logFilePath}"
                : string.Empty;
            MessageBox.Show(this, exception.Message + detail, "Run Game from TEMP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isTempRunPreparing = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            CancelButton.IsEnabled = _isBusy;
            ProgressBar.Value = 0;
            RunGameFromTempMenuItem.IsEnabled = !_isBusy && _session is not null;
            UpdateToolsMenuVisibility();
        }
    }

    private string? SelectRunnerForTempRun(IReadOnlyList<TempGameRunnerCandidate> candidates)
    {
        TempGameRunnerCandidate[] preferred = candidates
            .Where(candidate => candidate.IsPreferred)
            .ToArray();
        if (preferred.Length == 1)
            return preferred[0].Path;

        if (candidates.Count == 1)
            return candidates[0].Path;

        string initialDirectory = candidates.Count > 0
            ? Path.GetDirectoryName(candidates[0].Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.GetDirectoryName(_session?.Info.OriginalInput ?? string.Empty) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        OpenFileDialog dialog = new()
        {
            Title = candidates.Count > 1
                ? "Select the original GameMaker runner executable"
                : "Select a compatible GameMaker runner executable",
            Filter = "Windows executables|*.exe|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (candidates.Count > 1)
        {
            AppendLog(LogMessage.Warning(
                $"Runner discovery found {candidates.Count:N0} possible executables. Asking for an explicit selection."));
            foreach (TempGameRunnerCandidate candidate in candidates.Take(20))
                AppendLog(LogMessage.Info($"Runner candidate: {candidate.Path} ({candidate.Reason})"));
        }
        else
        {
            AppendLog(LogMessage.Warning("No runner executable could be discovered automatically. Asking for an explicit selection."));
        }

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OpenCurrentTempRunFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentTempRunDirectory) && Directory.Exists(_currentTempRunDirectory))
            Process.Start(new ProcessStartInfo(_currentTempRunDirectory) { UseShellExecute = true });
    }

    private void CleanOldTempRunFoldersMenuItem_Click(object sender, RoutedEventArgs e)
    {
        TempGameRunCleanupResult result = GameProjectSession.CleanOldTempRunFolders(TimeSpan.FromDays(7));
        StatusTextBlock.Text = "Old TEMP run cleanup complete.";
        StatusDetailTextBlock.Text =
            $"{result.DirectoriesRemoved:N0} removed | {result.DirectoriesSkipped:N0} skipped";
        foreach (string error in result.Errors.Take(20))
            AppendLog(LogMessage.Warning("TEMP cleanup: " + error));
        MessageBox.Show(this,
            $"TEMP run root: {result.RootDirectory}\n\nRemoved: {result.DirectoriesRemoved:N0}\nSkipped: {result.DirectoriesSkipped:N0}\nErrors: {result.Errors.Count:N0}",
            "Clean Old TEMP Run Folders",
            MessageBoxButton.OK,
            result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
