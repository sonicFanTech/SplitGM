#nullable enable

using SplitGM.Core;
using System.Windows;
using System.Windows.Input;

namespace SplitGM.Gui;

public partial class ConnectedCodeWindow : Window
{
    public int SelectedCodeIndex { get; private set; } = -1;

    public ConnectedCodeWindow(string sourceName, IReadOnlyList<ConnectedCodeInfo> codeEntries)
    {
        InitializeComponent();
        TitleText.Text = $"Connected GML Code — {sourceName}";
        SubtitleText.Text = codeEntries.Count == 0
            ? "No directly connected VM code entries were found."
            : $"{codeEntries.Count:N0} connected code entr{(codeEntries.Count == 1 ? "y" : "ies")} found.";
        CodeGrid.ItemsSource = codeEntries;
        if (codeEntries.Count > 0)
            CodeGrid.SelectedIndex = 0;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (CodeGrid.SelectedItem is not ConnectedCodeInfo selected)
            return;
        SelectedCodeIndex = selected.CodeIndex;
        DialogResult = true;
    }

    private void CodeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
