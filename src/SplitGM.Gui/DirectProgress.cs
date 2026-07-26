#nullable enable

namespace SplitGM.Gui;

/// <summary>
/// An IProgress implementation that does not capture SynchronizationContext.
/// The receiver is responsible for thread-safe coalescing or dispatching.
/// </summary>
internal sealed class DirectProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public DirectProgress(Action<T> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
