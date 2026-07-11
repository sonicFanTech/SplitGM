#nullable enable

using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SplitGM.Gui;

public partial class SettingsWindow : Window
{
    private readonly SplitGmSettings _settings;

    public SettingsWindow(SplitGmSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ConfirmOverwriteCheckBox.IsChecked = settings.ConfirmOverwrite;
        OpenOutputCheckBox.IsChecked = settings.OpenOutputAfterExport;
        ShowOperationWindowCheckBox.IsChecked = settings.ShowOperationWindow;
        AutoCloseOperationWindowCheckBox.IsChecked = settings.AutoCloseOperationWindow;
        DefaultExportDirectoryTextBox.Text = settings.DefaultExportDirectory;
        RelationshipLimitTextBox.Text = settings.RelationshipResultLimit.ToString();
        LineNumbersCheckBox.IsChecked = settings.ShowLineNumbers;
        WordWrapCheckBox.IsChecked = settings.WordWrap;
        RememberWindowCheckBox.IsChecked = settings.RememberWindowPlacement;
        ExportResourcesCheckBox.IsChecked = settings.ExportResources;
        ExportAssemblyCheckBox.IsChecked = settings.ExportAssembly;
        ExportIndexesCheckBox.IsChecked = settings.ExportIndexes;
        SettingsPathTextBox.Text = settings.SettingsPath;
    }

    private void BrowseExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Choose the default SplitGM export directory",
            InitialDirectory = Directory.Exists(DefaultExportDirectoryTextBox.Text)
                ? DefaultExportDirectoryTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) == true)
            DefaultExportDirectoryTextBox.Text = dialog.FolderName;
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        string? directory = Path.GetDirectoryName(_settings.SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RelationshipLimitTextBox.Text, out int relationshipLimit) || relationshipLimit is < 100 or > 10000)
        {
            MessageBox.Show(this, "Relationship result limit must be between 100 and 10,000.",
                "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.ConfirmOverwrite = ConfirmOverwriteCheckBox.IsChecked == true;
        _settings.OpenOutputAfterExport = OpenOutputCheckBox.IsChecked == true;
        _settings.ShowOperationWindow = ShowOperationWindowCheckBox.IsChecked == true;
        _settings.AutoCloseOperationWindow = AutoCloseOperationWindowCheckBox.IsChecked == true;
        _settings.DefaultExportDirectory = DefaultExportDirectoryTextBox.Text.Trim();
        _settings.RelationshipResultLimit = relationshipLimit;
        _settings.ShowLineNumbers = LineNumbersCheckBox.IsChecked == true;
        _settings.WordWrap = WordWrapCheckBox.IsChecked == true;
        _settings.RememberWindowPlacement = RememberWindowCheckBox.IsChecked == true;
        _settings.ExportResources = ExportResourcesCheckBox.IsChecked == true;
        _settings.ExportAssembly = ExportAssemblyCheckBox.IsChecked == true;
        _settings.ExportIndexes = ExportIndexesCheckBox.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
