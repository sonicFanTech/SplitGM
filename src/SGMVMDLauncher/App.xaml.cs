#nullable enable

using System.Threading;
using System.Windows;

namespace SGMVMDLauncher;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool createdNew;
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\SplitGM.SGMVMDLauncher",
            createdNew: out createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "SplitGM is already starting. Please wait for the existing launcher.",
                "SplitGM Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        LauncherWindow window = new();
        MainWindow = window;
        window.Show();

        try
        {
            await window.RunAsync(e.Args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The SplitGM launcher encountered an unexpected error.\n\n{ex.Message}",
                "SplitGM Launcher error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may not be owned if startup failed before acquisition.
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
