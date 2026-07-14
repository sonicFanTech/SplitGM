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
            "SplitGM v0.5 adds a versioned .splitgmproj intermediate format and an experimental, " +
            "repair-oriented reconstructed GameMaker .yyp project export.";
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
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_reconstructionWindow, window))
                _reconstructionWindow = null;
        };
        window.Show();
    }

    private Progress<ReconstructionProgress> CreateReconstructionProgress()
    {
        return new Progress<ReconstructionProgress>(update =>
        {
            StatusTextBlock.Text = update.Message;
            ProgressBar.Value = update.Total > 0 ? update.Percentage : 0;
            _reconstructionWindow?.UpdateProgress(update);
        });
    }

    private Progress<LogMessage> CreateReconstructionLog()
    {
        return new Progress<LogMessage>(message =>
        {
            AppendLog(message);
            _reconstructionWindow?.AppendLog(message);
        });
    }

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
        if (_session is null || _isBusy)
            return;

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
        Progress<ReconstructionProgress> progress = CreateReconstructionProgress();
        Progress<LogMessage> log = CreateReconstructionLog();

        try
        {
            CancelCurrentPreview();
            await _session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            ReconstructedProjectResult result = await _session.ExportReconstructedProjectAsync(
                new ReconstructedProjectOptions(
                    outputDirectory,
                    overwrite,
                    ExportRawFallbacks: true,
                    ExportAssemblyFallbacks: _settings.ExportAssembly,
                    ValidateOutput: true),
                progress,
                log,
                _operationCancellation.Token);

            _lastOutputDirectory = result.OutputDirectory;
            OpenOutputButton.IsEnabled = true;
            ProgressBar.Value = 100;
            StatusTextBlock.Text = "Reconstructed .yyp project completed.";
            StatusDetailTextBlock.Text =
                $"{result.ResourcesRepresented:N0} represented • {result.ResourcesPreservedAsFallback:N0} fallback • " +
                $"{result.WarningCount:N0} warnings • {result.ErrorCount:N0} errors";

            string summary = result.ErrorCount == 0
                ? $"Reconstructed project completed: {result.ResourcesRepresented:N0} resources represented and {result.ResourcesPreservedAsFallback:N0} preserved as fallback data."
                : $"Reconstructed project completed with {result.ErrorCount:N0} error(s). Review the validation report before opening it in GameMaker.";
            CompleteReconstructionWindow(result.ErrorCount == 0, summary);

            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputDirectory))
                Process.Start(new ProcessStartInfo(result.OutputDirectory) { UseShellExecute = true });

            MessageBox.Show(this,
                $"The reconstructed GameMaker project has been written.\n\n" +
                $"Project: {Path.GetFileName(result.ProjectFile)}\n" +
                $"Target: {result.TargetProfile}\n" +
                $"Represented in .yyp: {result.ResourcesRepresented:N0}\n" +
                $"Fallback-only resources: {result.ResourcesPreservedAsFallback:N0}\n" +
                $"Warnings: {result.WarningCount:N0}\n" +
                $"Errors: {result.ErrorCount:N0}\n\n" +
                "This is a transparent repair workspace, not an identical copy of the original project. Read SplitGM-Reconstruction-Validation.txt before trying to compile it.",
                "Reconstructed .yyp export",
                MessageBoxButton.OK,
                result.ErrorCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
}
