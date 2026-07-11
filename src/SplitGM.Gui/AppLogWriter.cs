#nullable enable

using System;
using System.IO;
using System.Text;
using SplitGM.Core;

namespace SplitGM.Gui;

internal sealed class AppLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public AppLogWriter()
    {
        string preferredDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
        string logDirectory;
        try
        {
            Directory.CreateDirectory(preferredDirectory);
            string probe = Path.Combine(preferredDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            logDirectory = preferredDirectory;
        }
        catch
        {
            logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SplitGM-VM Decompiler",
                "Logs");
            Directory.CreateDirectory(logDirectory);
        }

        LogPath = Path.Combine(logDirectory, $"SplitGM-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        _writer = new StreamWriter(LogPath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        _writer.WriteLine($"{SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}");
        _writer.WriteLine($"Started: {DateTimeOffset.Now:O}");
        _writer.WriteLine($"Runtime: {Environment.Version}");
        _writer.WriteLine($"OS: {Environment.OSVersion}");
        _writer.WriteLine(new string('=', 72));
    }

    public string LogPath { get; }

    public void Write(LogMessage message)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _writer.WriteLine(
                $"[{message.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] " +
                $"{message.Level.ToString().ToUpperInvariant().PadRight(7)} {message.Text}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
