#nullable enable

using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SGMVMDLauncher.Playback;

namespace SGMVMDLauncher;

public partial class LauncherWindow : Window
{
    private const int FrameCount = 412;
    private const double FramesPerSecond = 24.0;
    private const int DecodePixelWidth = 960;

    // Trailer ending split points selected from the original 412-frame sequence.
    private const int FirstHalfEndFrame = 180;
    private const int SecondHalfStartFrame = 185;
    private const int FirstHalfStaticFrame = 145;
    private const int SecondHalfStaticFrame = 385;
    private static readonly TimeSpan StaticFrameMinimumDuration = TimeSpan.FromSeconds(2);

    private EmbeddedFrameSequencePlayer? _player;
    private CancellationTokenSource? _staticDisplayCancellation;
    private string[] _forwardedArguments = [];
    private bool _launchInProgress;
    private bool _shutdownRequested;

    public LauncherWindow()
    {
        InitializeComponent();
    }

    public async Task RunAsync(string[] forwardedArguments)
    {
        _forwardedArguments = forwardedArguments ?? [];

        // Let WPF paint the first black surface before decoding frame one.
        await Dispatcher.Yield(DispatcherPriority.Loaded);

        try
        {
            StartupDisplayMode displayMode = StartupDisplaySettings.Load();
            await DisplayConfiguredStartupAsync(displayMode);
            await StartSplitGMAsync();
        }
        catch (OperationCanceledException) when (_shutdownRequested)
        {
            // The user closed the launcher while the animation was active.
        }
        catch (Exception ex)
        {
            ShowLauncherError(ex);
        }
    }

    private Task DisplayConfiguredStartupAsync(StartupDisplayMode mode) =>
        mode switch
        {
            StartupDisplayMode.FirstHalf => PlayRangeAsync(1, FirstHalfEndFrame),
            StartupDisplayMode.SecondHalf => PlayRangeAsync(SecondHalfStartFrame, FrameCount),
            StartupDisplayMode.FirstHalfStatic => ShowStaticFrameAsync(FirstHalfStaticFrame),
            StartupDisplayMode.SecondHalfStatic => ShowStaticFrameAsync(SecondHalfStaticFrame),
            _ => PlayRangeAsync(1, FrameCount)
        };

    private async Task PlayRangeAsync(int firstFrame, int lastFrame)
    {
        _player?.Dispose();
        _player = new EmbeddedFrameSequencePlayer(
            FrameImage,
            Assembly.GetExecutingAssembly(),
            "SGMVMDLauncher.Frames",
            firstFrame,
            lastFrame,
            FramesPerSecond,
            DecodePixelWidth);

        await _player.PlayAsync();
    }

    private async Task ShowStaticFrameAsync(int frameNumber)
    {
        _player?.Dispose();
        _player = new EmbeddedFrameSequencePlayer(
            FrameImage,
            Assembly.GetExecutingAssembly(),
            "SGMVMDLauncher.Frames",
            FrameCount,
            FramesPerSecond,
            DecodePixelWidth);
        _player.ShowFrame(frameNumber);

        _staticDisplayCancellation?.Dispose();
        _staticDisplayCancellation = new CancellationTokenSource();

        try
        {
            await Task.Delay(StaticFrameMinimumDuration, _staticDisplayCancellation.Token);
        }
        catch (OperationCanceledException) when (!_shutdownRequested)
        {
            // Space, Enter, or Escape skips the minimum static-frame hold.
        }
        finally
        {
            _staticDisplayCancellation?.Dispose();
            _staticDisplayCancellation = null;
        }
    }

    private async Task StartSplitGMAsync()
    {
        if (_launchInProgress || _shutdownRequested)
            return;

        _launchInProgress = true;
        RetryButton.IsEnabled = false;

        try
        {
            await AuthorizedSplitGmProcess.StartAndWaitUntilReadyAsync(_forwardedArguments);
            _shutdownRequested = true;
            Close();
            Application.Current.Shutdown(0);
        }
        catch
        {
            _launchInProgress = false;
            RetryButton.IsEnabled = true;
            throw;
        }
    }

    private void ShowLauncherError(Exception exception)
    {
        _staticDisplayCancellation?.Cancel();
        _player?.Dispose();
        _player = null;
        Topmost = false;

        ErrorText.Text = exception.Message +
            "\n\nThe animation frames are embedded in the launcher. No external frame folder is required.";
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter or Key.Escape)
        {
            _player?.Skip();
            _staticDisplayCancellation?.Cancel();
            e.Handled = true;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            Topmost = true;
            await StartSplitGMAsync();
        }
        catch (Exception ex)
        {
            ShowLauncherError(ex);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _shutdownRequested = true;
        _staticDisplayCancellation?.Cancel();
        Close();
        Application.Current.Shutdown(1);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _staticDisplayCancellation?.Cancel();
        _staticDisplayCancellation?.Dispose();
        _staticDisplayCancellation = null;

        _player?.Dispose();
        _player = null;

        if (!_shutdownRequested)
        {
            _shutdownRequested = true;
            Application.Current.Shutdown(1);
        }
    }
}
