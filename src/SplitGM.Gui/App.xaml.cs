#nullable enable

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SplitGM.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        base.OnStartup(e);

        // Create the main window manually rather than through StartupUri. When a WPF
        // constructor or InitializeComponent() fails, this preserves the real inner
        // exception instead of leaving only the generic XamlParseException wrapper.
        try
        {
            SplashScreen? splash = null;
            try
            {
                splash = new SplashScreen("Resources/Brand/SplitGM_SPLASH.png");
                splash.Show(autoClose: false, topMost: true);
            }
            catch
            {
                // A missing splash resource must never prevent the main application from opening.
            }

            SplitGM.Gui.MainWindow window = new();
            MainWindow = window;
            window.Show();
            splash?.Close(TimeSpan.FromMilliseconds(250));
        }
        catch (Exception ex)
        {
            ShowFatalError(ex, "Application startup", shutDown: true);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception, "UI thread", shutDown: false);
        e.Handled = true;
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashReport(e.Exception, "Background task");
        e.SetObserved();
    }

    private void ShowFatalError(Exception exception, string source, bool shutDown)
    {
        string path;
        try
        {
            path = WriteCrashReport(exception, source);
        }
        catch (Exception reportException)
        {
            path = "The crash report could not be written: " + reportException.Message;
        }

        Exception root = GetDeepestException(exception);
        MessageBoxResult result = MessageBox.Show(
            $"SplitGM encountered an unexpected error.\n\n" +
            $"Actual error: {root.GetType().Name}: {root.Message}\n\n" +
            $"Crash report:\n{path}\n\n" +
            "Open the crash report now?",
            "SplitGM unexpected error",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes && File.Exists(path))
            TryOpenFile(path);

        if (shutDown)
            Shutdown(-1);
    }

    private static Exception GetDeepestException(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
            current = current.InnerException;
        return current;
    }

    private static string WriteCrashReport(Exception exception, string source)
    {
        string directory = GetCrashDirectory();
        string path = Path.Combine(directory, $"Crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.log");

        StringBuilder report = new();
        report.AppendLine($"SplitGM-VM Decompiler {SplitGM.Core.SplitGmProduct.DisplayVersion}");
        report.AppendLine($"Time: {DateTimeOffset.Now:O}");
        report.AppendLine($"Source: {source}");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine($"Runtime: {Environment.Version}");
        report.AppendLine($"Base directory: {AppContext.BaseDirectory}");
        report.AppendLine(new string('=', 88));
        report.AppendLine();

        int level = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            report.AppendLine($"Exception level {level}: {current.GetType().FullName}");
            report.AppendLine(current.Message);
            report.AppendLine(current.StackTrace ?? "<no stack trace>");

            if (current is ReflectionTypeLoadException reflection && reflection.LoaderExceptions is not null)
            {
                report.AppendLine("Loader exceptions:");
                foreach (Exception? loader in reflection.LoaderExceptions)
                {
                    if (loader is not null)
                        report.AppendLine(loader.ToString());
                }
            }

            report.AppendLine();
            level++;
        }

        report.AppendLine("Complete exception text:");
        report.AppendLine(exception.ToString());
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string GetCrashDirectory()
    {
        string preferred = Path.Combine(AppContext.BaseDirectory, "Logs", "CrashReports");
        try
        {
            Directory.CreateDirectory(preferred);
            string probe = Path.Combine(preferred, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SplitGM-VM-Decompiler",
                "CrashLogs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static void TryOpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // The path remains visible in the error dialog if Windows cannot open it.
        }
    }
}
