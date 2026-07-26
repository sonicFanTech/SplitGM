#nullable enable

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SGMVMDLauncher.Playback;

public enum FramePlaybackResult
{
    Completed,
    Skipped
}

/// <summary>
/// Plays an ordered JPEG sequence embedded in a caller-provided assembly.
/// Only the currently displayed BitmapSource is retained; the full sequence is
/// never preloaded into memory.
/// </summary>
public sealed class EmbeddedFrameSequencePlayer : IDisposable
{
    private readonly Image _target;
    private readonly Assembly _resourceAssembly;
    private readonly string _resourcePrefix;
    private readonly int _firstFrame;
    private readonly int _lastFrame;
    private readonly double _framesPerSecond;
    private readonly int _decodePixelWidth;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    private TaskCompletionSource<FramePlaybackResult>? _completion;
    private int _displayedFrame;
    private bool _disposed;

    public EmbeddedFrameSequencePlayer(
        Image target,
        Assembly resourceAssembly,
        string resourcePrefix,
        int frameCount,
        double framesPerSecond,
        int decodePixelWidth)
        : this(
            target: target,
            resourceAssembly: resourceAssembly,
            resourcePrefix: resourcePrefix,
            firstFrame: 1,
            lastFrame: frameCount,
            framesPerSecond: framesPerSecond,
            decodePixelWidth: decodePixelWidth)
    {
    }

    public EmbeddedFrameSequencePlayer(
        Image target,
        Assembly resourceAssembly,
        string resourcePrefix,
        int firstFrame,
        int lastFrame,
        double framesPerSecond,
        int decodePixelWidth)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lastFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodePixelWidth);

        if (lastFrame < firstFrame)
            throw new ArgumentOutOfRangeException(nameof(lastFrame), "The last frame cannot be before the first frame.");

        _target = target;
        _resourceAssembly = resourceAssembly;
        _resourcePrefix = resourcePrefix.TrimEnd('.');
        _firstFrame = firstFrame;
        _lastFrame = lastFrame;
        _framesPerSecond = framesPerSecond;
        _decodePixelWidth = decodePixelWidth;

        _timer = new DispatcherTimer(DispatcherPriority.Render, target.Dispatcher)
        {
            // A timer faster than the source frame rate lets the Stopwatch select
            // the correct frame without accumulating one-tick-at-a-time drift.
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _timer.Tick += Timer_Tick;
    }

    public Task<FramePlaybackResult> PlayAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completion is not null)
            throw new InvalidOperationException("This frame player instance has already been started.");

        ValidateSequence();
        DisplayFrame(_firstFrame);

        _completion = new TaskCompletionSource<FramePlaybackResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _clock.Restart();
        _timer.Start();
        return _completion.Task;
    }

    /// <summary>
    /// Displays one embedded frame without starting timed playback.
    /// </summary>
    public void ShowFrame(int frameNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completion is not null)
            throw new InvalidOperationException("A static frame cannot be selected after playback has started.");
        if (frameNumber < _firstFrame || frameNumber > _lastFrame)
            throw new ArgumentOutOfRangeException(nameof(frameNumber));

        DisplayFrame(frameNumber);
    }

    public void Skip()
    {
        if (_completion is null || _completion.Task.IsCompleted)
            return;

        Complete(FramePlaybackResult.Skipped);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_completion is null || _completion.Task.IsCompleted)
            return;

        int targetFrame = _firstFrame +
            (int)Math.Floor(_clock.Elapsed.TotalSeconds * _framesPerSecond);

        if (targetFrame > _lastFrame)
        {
            Complete(FramePlaybackResult.Completed);
            return;
        }

        if (targetFrame == _displayedFrame)
            return;

        // When rendering is temporarily delayed, jump directly to the frame that
        // belongs at the current playback time instead of extending the animation.
        try
        {
            DisplayFrame(targetFrame);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _clock.Stop();
            _completion.TrySetException(ex);
        }
    }

    private void DisplayFrame(int frameNumber)
    {
        string resourceName = GetResourceName(frameNumber);
        using Stream stream = _resourceAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded startup frame '{resourceName}' could not be opened.");

        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // Do not use IgnoreImageCache with StreamSource. WPF attempts to
        // remove a null UriSource from its image cache and throws
        // ArgumentNullException (Parameter "key"). Each frame is loaded from
        // a fresh embedded stream, so URI caching is not involved anyway.
        bitmap.CreateOptions = BitmapCreateOptions.None;
        bitmap.DecodePixelWidth = _decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        _target.Source = bitmap;
        _displayedFrame = frameNumber;
    }

    private void ValidateSequence()
    {
        using Stream? first = _resourceAssembly.GetManifestResourceStream(GetResourceName(_firstFrame));
        using Stream? last = _resourceAssembly.GetManifestResourceStream(GetResourceName(_lastFrame));

        if (first is null || last is null)
        {
            throw new InvalidOperationException(
                $"The embedded JPEG sequence is incomplete. Expected frames {_firstFrame:0000} through {_lastFrame:0000}.");
        }
    }

    private string GetResourceName(int frameNumber) =>
        $"{_resourcePrefix}.frame_{frameNumber:0000}.jpg";

    private void Complete(FramePlaybackResult result)
    {
        _timer.Stop();
        _clock.Stop();
        _completion?.TrySetResult(result);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _clock.Stop();
        _target.Source = null;
        _completion?.TrySetCanceled();
    }
}
