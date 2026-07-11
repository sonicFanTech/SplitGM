#nullable enable

using Microsoft.Win32;
using SplitGM.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SplitGM.Gui;

public partial class MainWindow
{
    private readonly SplitGmSettings _settings = SplitGmSettings.Load();
    private OperationProgressWindow? _operationWindow;
    private ExplorerNode? _selectedExplorerNode;

    private void InitializeV04()
    {
        LineNumbersCheckBox.IsChecked = _settings.ShowLineNumbers;
        WordWrapCheckBox.IsChecked = _settings.WordWrap;
        OpenLastGameMenuItem.IsEnabled = File.Exists(_settings.LastOpenedGame);
        RelationshipSummaryTextBox.Text =
            "Select a code entry or resource, then choose Tools > Analyze selected relationships.\r\n\r\n" +
            "SplitGM v0.4 can resolve callers/callees, object inheritance, room instances, room transitions, " +
            "asset references, global variables, and heuristic unused-resource candidates.";

        if (_settings.RememberWindowPlacement)
        {
            Width = Math.Max(MinWidth, _settings.WindowWidth);
            Height = Math.Max(MinHeight, _settings.WindowHeight);
            if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
            {
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            if (_settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
    }

    private void StartOperationWindow(string title, string description, string path)
    {
        CloseOperationWindow(force: true);
        if (!_settings.ShowOperationWindow)
            return;

        OperationProgressWindow window = new(title, description, path)
        {
            Owner = this
        };
        _operationWindow = window;
        window.CancelRequested += (_, _) => _operationCancellation?.Cancel();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_operationWindow, window))
                _operationWindow = null;
        };
        window.Show();
    }

    private Progress<DecompileProgress> CreateDetailedProgress()
    {
        return new Progress<DecompileProgress>(update =>
        {
            StatusTextBlock.Text = update.Message;
            ProgressBar.Value = update.Total > 0 ? update.Percentage : 0;
            _operationWindow?.UpdateProgress(update);
        });
    }

    private Progress<LogMessage> CreateDetailedLog()
    {
        return new Progress<LogMessage>(message =>
        {
            AppendLog(message);
            _operationWindow?.AppendLog(message);
        });
    }

    private void CompleteOperationWindow(bool success, string summary)
    {
        if (_operationWindow is null)
            return;
        _operationWindow.Complete(success, summary, success && _settings.AutoCloseOperationWindow);
        if (success && _settings.AutoCloseOperationWindow)
            _operationWindow = null;
    }

    private void CloseOperationWindow(bool force)
    {
        if (_operationWindow is null)
            return;
        if (force)
        {
            _operationWindow.Complete(false, "Operation window closed.", autoClose: true);
            _operationWindow = null;
        }
    }

    private async void OpenLastGameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy && File.Exists(_settings.LastOpenedGame))
            await LoadGameAsync(_settings.LastOpenedGame);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow dialog = new(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        ApplySettingsToView();
        StatusTextBlock.Text = $"Settings saved to {_settings.SettingsPath}.";
    }

    private void ApplySettingsToView()
    {
        LineNumbersCheckBox.IsChecked = _settings.ShowLineNumbers;
        WordWrapCheckBox.IsChecked = _settings.WordWrap;
        RenderCurrentCodeView();
        if (_currentResourcePreview is not null)
            ApplyResourcePreview(_currentResourcePreview);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string? directory = Path.GetDirectoryName(_appLog.LogPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        AboutWindow dialog = new() { Owner = this };
        dialog.ShowDialog();
    }

    private async void ExportResourceTypeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _isBusy || sender is not MenuItem { Tag: string kindName } ||
            !Enum.TryParse(kindName, ignoreCase: false, out ResourceKind kind))
        {
            return;
        }

        IReadOnlyList<ResourceEntryInfo> entries = _session.GetResourceEntries(kind);
        if (entries.Count == 0)
        {
            MessageBox.Show(this,
                $"This game does not contain any {kind} resources.",
                "Nothing to export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = $"Export all {kind}",
            InitialDirectory = string.IsNullOrWhiteSpace(_settings.DefaultExportDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : _settings.DefaultExportDirectory
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, $"Exporting all {kind}...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            $"Export all {kind}",
            $"SplitGM is exporting every recoverable {kind} resource and its metadata.",
            dialog.FolderName);
        Progress<DecompileProgress> progress = CreateDetailedProgress();

        try
        {
            CancelCurrentPreview();
            await _session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            ResourceExportResult result = await _session.ExportResourceCategoryAsync(
                kind,
                dialog.FolderName,
                progress,
                _operationCancellation.Token);

            _lastOutputDirectory = result.OutputPath;
            OpenOutputButton.IsEnabled = true;
            ProgressBar.Value = 100;
            StatusTextBlock.Text = $"Exported all {kind}.";
            StatusDetailTextBlock.Text = $"{entries.Count:N0} resources • {result.FilesWritten:N0} files • {FormatBytes(result.BytesWritten)}";
            CompleteOperationWindow(true,
                $"Exported {entries.Count:N0} {kind} resource(s) into {result.FilesWritten:N0} file(s).");

            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputPath))
                Process.Start(new ProcessStartInfo(result.OutputPath) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"{kind} export cancelled.";
            CompleteOperationWindow(false, $"{kind} export cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            MessageBox.Show(this, exception.Message, $"{kind} export error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = $"{kind} export failed.";
            CompleteOperationWindow(false, $"{kind} export failed.");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void ViewTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
            return;
        MainTabControl.SelectedItem = tag switch
        {
            "Preview" => PreviewTab,
            "GML" => GmlTab,
            "Assembly" => AssemblyTab,
            "Details" => DetailsTab,
            "Relationships" => RelationshipsTab,
            "Search" => SearchTab,
            "Activity" => ActivityTab,
            _ => MainTabControl.SelectedItem
        };
    }


    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            OpenGameButton_Click(this, new RoutedEventArgs());
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            ShowSearchTab_Click(this, new RoutedEventArgs());
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            e.Handled = true;
            CopyCurrentViewButton_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            RefreshCurrentPreview_Click(this, new RoutedEventArgs());
        }
    }

    private void ShowSearchTab_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedItem = SearchTab;
        CodeSearchTextBox.Focus();
        CodeSearchTextBox.SelectAll();
    }

    private async void RefreshCurrentPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _session is null)
            return;
        if (_currentCodeView is not null)
            await DisplayCodeEntryAsync(_currentCodeView.Entry.Index);
        else if (_currentResourceKind is ResourceKind kind && _currentResourceIndex >= 0)
            await DisplayResourceAsync(kind, _currentResourceIndex, CurrentItemTitle.Text, _currentSpriteFrame);
    }

    private async void AnalyzeSelectedRelationships_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _isBusy)
            return;
        if (_currentCodeView is null && (_currentResourceKind is null || _currentResourceIndex < 0))
        {
            MessageBox.Show(this, "Select a code entry or resource first.", "Relationship analysis",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Analyzing relationships...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Relationship analysis",
            "SplitGM is resolving direct model links and heuristic code references.",
            CurrentItemTitle.Text);
        Progress<DecompileProgress> progress = CreateDetailedProgress();
        MainTabControl.SelectedItem = RelationshipsTab;
        RelationshipGrid.ItemsSource = null;
        RelationshipSummaryTextBox.Text = "Analyzing relationships...";

        try
        {
            RelationshipAnalysisResult result = _currentCodeView is not null
                ? await _session.AnalyzeCodeRelationshipsAsync(
                    _currentCodeView.Entry.Index,
                    _settings.RelationshipResultLimit,
                    progress,
                    _operationCancellation.Token)
                : await _session.AnalyzeResourceRelationshipsAsync(
                    _currentResourceKind!.Value,
                    _currentResourceIndex,
                    _settings.RelationshipResultLimit,
                    progress,
                    _operationCancellation.Token);

            RelationshipSummaryTextBox.Text = result.Summary +
                (result.Warnings.Count == 0 ? string.Empty : "\r\nWarnings\r\n--------\r\n" + string.Join("\r\n", result.Warnings.Select(item => "- " + item)));
            RelationshipGrid.ItemsSource = result.Entries;
            StatusTextBlock.Text = $"Relationship analysis found {result.Entries.Count:N0} entries.";
            ProgressBar.Value = 100;
            CompleteOperationWindow(true, $"Found {result.Entries.Count:N0} relationship entries.");
        }
        catch (OperationCanceledException)
        {
            RelationshipSummaryTextBox.Text = "Relationship analysis cancelled.";
            StatusTextBlock.Text = "Relationship analysis cancelled.";
            CompleteOperationWindow(false, "Relationship analysis cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            RelationshipSummaryTextBox.Text = exception.ToString();
            StatusTextBlock.Text = "Relationship analysis failed.";
            CompleteOperationWindow(false, "Relationship analysis failed.");
            MessageBox.Show(this, exception.Message, "Relationship analysis error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void AnalyzeUnusedResources_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _isBusy)
            return;

        SetBusy(true, "Finding unused-resource candidates...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Unused-resource analysis",
            "SplitGM is building a heuristic reference index. Dynamic lookups can produce false positives.",
            _session.Info.DisplayName);
        Progress<DecompileProgress> progress = CreateDetailedProgress();
        MainTabControl.SelectedItem = RelationshipsTab;
        RelationshipGrid.ItemsSource = null;
        RelationshipSummaryTextBox.Text = "Building a project-wide reference index...";

        try
        {
            UnusedResourceReport report = await _session.AnalyzeUnusedResourcesAsync(
                Math.Max(_settings.RelationshipResultLimit, 1000),
                progress,
                _operationCancellation.Token);
            RelationshipEntry[] entries = report.Candidates.Select(candidate => new RelationshipEntry(
                RelationshipKind.UnusedCandidate,
                "Unused candidate",
                candidate.ResourceKind.ToString(),
                candidate.Name,
                $"{candidate.Reason} Confidence: {candidate.Confidence}.",
                ResourceKind: candidate.ResourceKind,
                ResourceIndex: candidate.ResourceIndex)).ToArray();
            RelationshipGrid.ItemsSource = entries;
            RelationshipSummaryTextBox.Text = report.Summary + "\r\n\r\n" + string.Join("\r\n", report.Warnings.Select(item => "- " + item));
            StatusTextBlock.Text = $"Found {entries.Length:N0} unused-resource candidates.";
            ProgressBar.Value = 100;
            CompleteOperationWindow(true, $"Found {entries.Length:N0} heuristic candidates.");
        }
        catch (OperationCanceledException)
        {
            RelationshipSummaryTextBox.Text = "Unused-resource analysis cancelled.";
            CompleteOperationWindow(false, "Unused-resource analysis cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            RelationshipSummaryTextBox.Text = exception.ToString();
            CompleteOperationWindow(false, "Unused-resource analysis failed.");
            MessageBox.Show(this, exception.Message, "Unused-resource analysis error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void RelationshipGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RelationshipGrid.SelectedItem is not RelationshipEntry relationship)
            return;
        if (relationship.CodeIndex is int codeIndex)
            await DisplayCodeEntryAsync(codeIndex);
        else if (relationship.ResourceKind is ResourceKind kind && relationship.ResourceIndex is int resourceIndex)
        {
            string name = _session?.GetResourceEntries(kind).FirstOrDefault(item => item.Index == resourceIndex)?.Name
                          ?? relationship.TargetName;
            await DisplayResourceAsync(kind, resourceIndex, name);
        }
    }

    private async void ResourceTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_session is null || _isBusy || _selectedExplorerNode is not ExplorerNode node)
            return;
        if (node.Kind == ExplorerNodeKind.CodeEntry)
        {
            await DisplayCodeEntryAsync(node.Index);
            return;
        }
        if (node.Kind == ExplorerNodeKind.ResourceEntry && node.ResourceKind == ResourceKind.Objects)
        {
            IReadOnlyList<ConnectedCodeInfo> connected = _session.GetConnectedCodeForObject(node.Index);
            await ShowConnectedCodeAsync(node.DisplayName, connected);
        }
    }

    private async void ObjectEventsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ObjectEventsGrid.SelectedItem is ObjectEventInfo { CodeIndex: >= 0 } selected)
            await DisplayCodeEntryAsync(selected.CodeIndex);
    }

    private async void RoomInstancesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_session is null || _currentResourceKind != ResourceKind.Rooms ||
            RoomInstancesGrid.SelectedItem is not RoomInstanceInfo instance)
            return;
        IReadOnlyList<ConnectedCodeInfo> connected = _session.GetConnectedCodeForRoomInstance(
            _currentResourceIndex,
            instance.InstanceId);
        await ShowConnectedCodeAsync($"{instance.ObjectName} — instance {instance.InstanceId}", connected);
    }

    private async Task ShowConnectedCodeAsync(string sourceName, IReadOnlyList<ConnectedCodeInfo> connected)
    {
        ConnectedCodeWindow dialog = new(sourceName, connected) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedCodeIndex >= 0)
            await DisplayCodeEntryAsync(dialog.SelectedCodeIndex);
    }

    private void SaveWindowSettings()
    {
        _settings.ShowLineNumbers = LineNumbersCheckBox.IsChecked == true;
        _settings.WordWrap = WordWrapCheckBox.IsChecked == true;
        if (_settings.RememberWindowPlacement)
        {
            System.Windows.Rect bounds = RestoreBounds;
            _settings.WindowWidth = Math.Max(MinWidth, bounds.Width);
            _settings.WindowHeight = Math.Max(MinHeight, bounds.Height);
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
        }
        _settings.Save();
    }
}
