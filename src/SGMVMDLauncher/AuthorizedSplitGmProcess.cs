#nullable enable

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SGMVMDLauncher;

internal static class AuthorizedSplitGmProcess
{
    private const string MainExecutableName = "SplitGM-VM-Decompiler.exe";
    private const string PipeArgument = "--splitgm-launch-pipe";
    private const string TokenEnvironmentVariable = "SPLITGM_LAUNCH_TOKEN";
    private const string LauncherPidEnvironmentVariable = "SPLITGM_LAUNCHER_PID";

    public static async Task StartAndWaitUntilReadyAsync(string[] forwardedArguments)
    {
        string mainExecutable = ResolveMainExecutable();
        string pipeName = $"SplitGM-Launch-{Guid.NewGuid():N}";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        using NamedPipeServerStream pipe = new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        ProcessStartInfo startInfo = new()
        {
            FileName = mainExecutable,
            WorkingDirectory = Path.GetDirectoryName(mainExecutable) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(PipeArgument);
        startInfo.ArgumentList.Add(pipeName);

        foreach (string argument in forwardedArguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment[TokenEnvironmentVariable] = token;
        startInfo.Environment[LauncherPidEnvironmentVariable] = Environment.ProcessId.ToString();

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not create the SplitGM process.");

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task connectionTask = pipe.WaitForConnectionAsync(timeout.Token);
        while (!connectionTask.IsCompleted)
        {
            await Task.WhenAny(
                connectionTask,
                Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{MainExecutableName} closed before it could verify the launcher. Exit code: {process.ExitCode}.");
            }
        }

        await connectionTask;

        using StreamReader reader = new(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using StreamWriter writer = new(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        string? hello = await reader.ReadLineAsync(timeout.Token);
        string expectedHello = $"HELLO|{token}|{process.Id}";
        if (!string.Equals(hello, expectedHello, StringComparison.Ordinal))
        {
            TryTerminate(process);
            throw new UnauthorizedAccessException(
                "SplitGM returned an invalid launcher authorization response.");
        }

        await writer.WriteLineAsync("OK".AsMemory(), timeout.Token);

        string? ready = await reader.ReadLineAsync(timeout.Token);
        string expectedReady = $"READY|{process.Id}";
        if (!string.Equals(ready, expectedReady, StringComparison.Ordinal))
        {
            TryTerminate(process);
            throw new InvalidOperationException(
                "SplitGM did not report that its main window was ready.");
        }
    }

    private static string ResolveMainExecutable()
    {
        string besideLauncher = Path.Combine(AppContext.BaseDirectory, MainExecutableName);
        if (File.Exists(besideLauncher))
            return besideLauncher;

        // Visual Studio keeps each project in a separate bin directory. These
        // fallbacks let SGMVMDLauncher remain the startup project during development.
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (string.Equals(current.Name, "SGMVMDLauncher", StringComparison.OrdinalIgnoreCase) &&
                current.Parent is not null)
            {
                string siblingGui = Path.Combine(current.Parent.FullName, "SplitGM.Gui", "bin");
                string[] configurations = ["Debug", "Release"];
                foreach (string configuration in configurations)
                {
                    string candidate = Path.Combine(
                        siblingGui,
                        configuration,
                        "net10.0-windows",
                        MainExecutableName);
                    if (File.Exists(candidate))
                        return candidate;
                }

                if (Directory.Exists(siblingGui))
                {
                    string? newestCandidate = Directory
                        .EnumerateFiles(
                            siblingGui,
                            MainExecutableName,
                            SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();

                    if (newestCandidate is not null)
                        return newestCandidate;
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"{MainExecutableName} was not found beside SGMVMDLauncher.exe. " +
            "Build the complete solution or run Build-Release.ps1 before starting the launcher.",
            besideLauncher);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The original authorization error is more useful than a cleanup error.
        }
    }
}
