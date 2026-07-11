#nullable enable

using SplitGM.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace SplitGM.Gui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = SplitGmProduct.DisplayVersion;
        RuntimeText.Text =
            $".NET {Environment.Version} • {RuntimeInformation.ProcessArchitecture} • " +
            $"{Environment.OSVersion.VersionString}";
    }

    private void OpenApplicationFolder_Click(object sender, RoutedEventArgs e)
    {
        string directory = AppContext.BaseDirectory;
        if (Directory.Exists(directory))
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OpenLicenseFiles_Click(object sender, RoutedEventArgs e)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"),
            Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")
        ];

        string? file = candidates.FirstOrDefault(File.Exists);
        if (file is not null)
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return;
        }

        MessageBox.Show(this,
            "The license files were not found beside this build. They are included in the complete SplitGM source package.",
            "License files unavailable",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
