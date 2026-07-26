#nullable enable

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace SplitGM.Gui;

internal sealed class LauncherAuthorizationException : Exception
{
    public LauncherAuthorizationException(string message)
        : base(message)
    {
    }

    public LauncherAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Holds the short-lived named-pipe connection created by SGMVMDLauncher.
/// The connection remains open until the main window is visible and READY is sent.
/// </summary>
internal sealed class LauncherAuthorizationSession : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private bool _readySent;

    private LauncherAuthorizationSession(
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer)
    {
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public static async Task<LauncherAuthorizationSession> OpenAsync(string[] arguments)
    {
        string pipeName = ReadArgument(arguments, "--splitgm-launch-pipe")
            ?? throw new LauncherAuthorizationException(
                "SplitGM was opened directly instead of through SGMVMDLauncher.exe.");

        string token = Environment.GetEnvironmentVariable("SPLITGM_LAUNCH_TOKEN")
            ?? throw new LauncherAuthorizationException(
                "The required launcher authorization token was not provided.");

        string launcherPidText = Environment.GetEnvironmentVariable("SPLITGM_LAUNCHER_PID")
            ?? throw new LauncherAuthorizationException(
                "The required launcher process information was not provided.");

        // The values are one-time startup data and are no longer needed after this read.
        Environment.SetEnvironmentVariable("SPLITGM_LAUNCH_TOKEN", null);
        Environment.SetEnvironmentVariable("SPLITGM_LAUNCHER_PID", null);

        if (!int.TryParse(launcherPidText, out int launcherPid) || launcherPid <= 0)
        {
            throw new LauncherAuthorizationException(
                "The launcher process information was invalid.");
        }

        ValidateLauncherProcess(launcherPid);

        NamedPipeClientStream pipe = new(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            StreamReader reader = new(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            StreamWriter writer = new(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(
                    $"HELLO|{token}|{Environment.ProcessId}".AsMemory(),
                    timeout.Token)
                .ConfigureAwait(false);

            string? response = await reader
                .ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!string.Equals(response, "OK", StringComparison.Ordinal))
            {
                reader.Dispose();
                writer.Dispose();
                throw new LauncherAuthorizationException(
                    "SGMVMDLauncher.exe rejected the SplitGM startup request.");
            }

            return new LauncherAuthorizationSession(pipe, reader, writer);
        }
        catch (LauncherAuthorizationException)
        {
            pipe.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            throw new LauncherAuthorizationException(
                "SplitGM could not verify a live SGMVMDLauncher.exe process.",
                ex);
        }
    }

    public async Task SignalReadyAsync()
    {
        if (_readySent)
            return;

        _readySent = true;
        await _writer.WriteLineAsync($"READY|{Environment.ProcessId}").ConfigureAwait(false);
    }

    private static string? ReadArgument(string[] arguments, string name)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                return arguments[index + 1];
        }

        return null;
    }

    private static void ValidateLauncherProcess(int launcherPid)
    {
        try
        {
            using Process launcher = Process.GetProcessById(launcherPid);
            if (launcher.HasExited ||
                !string.Equals(
                    launcher.ProcessName,
                    "SGMVMDLauncher",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherAuthorizationException(
                    "The authorizing SGMVMDLauncher.exe process is not running.");
            }
        }
        catch (LauncherAuthorizationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LauncherAuthorizationException(
                "The authorizing SGMVMDLauncher.exe process could not be verified.",
                ex);
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
    }
}
