using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace SplitGM.Core;

public sealed record TempGameRunnerCandidate(
    string Path,
    string Reason,
    bool IsPreferred);

public enum TempRunStage
{
    Requested,
    DetectingSteam,
    PreparingDirectory,
    WritingData,
    CopyingSidecars,
    Launching,
    WaitingForGameProcess,
    Started,
    TrackingExit,
    Completed,
    Failed,
    Cancelled
}

public sealed record TempRunProgress(
    TempRunStage Stage,
    string Message,
    long BytesCompleted = 0,
    long BytesTotal = 0,
    string? RelativePath = null)
{
    public double Percentage => BytesTotal <= 0
        ? 0
        : Math.Clamp((double)BytesCompleted / BytesTotal * 100.0, 0, 100);
}

public sealed record TempGameRunResult(
    string RunDirectory,
    string DataFilePath,
    string RunnerExecutablePath,
    string LogFilePath,
    string ManifestFilePath,
    string WorkingDirectory,
    string LaunchStrategy,
    int ProcessId,
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<string> CopiedSidecars,
    string? SteamAppId,
    string? SteamAppIdSource);

public sealed record TempGameRunCleanupResult(
    string RootDirectory,
    int DirectoriesRemoved,
    int DirectoriesSkipped,
    IReadOnlyList<string> Errors);

public sealed class TempGameRunException : Exception
{
    public TempGameRunException(string message, string? runDirectory = null, string? logFilePath = null)
        : base(message)
    {
        RunDirectory = runDirectory;
        LogFilePath = logFilePath;
    }

    public TempGameRunException(string message, Exception innerException, string? runDirectory = null, string? logFilePath = null)
        : base(message, innerException)
    {
        RunDirectory = runDirectory;
        LogFilePath = logFilePath;
    }

    public string? RunDirectory { get; }
    public string? LogFilePath { get; }
}

public sealed class TempRunManifest
{
    public string Format { get; set; } = "SplitGM TEMP Run Manifest";
    public string FormatVersion { get; set; } = "1.0";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? PreparedAt { get; set; }
    public DateTimeOffset? LaunchedAt { get; set; }
    public DateTimeOffset? GameExitedAt { get; set; }
    public string SourceDataPath { get; set; } = string.Empty;
    public string TempDataPath { get; set; } = string.Empty;
    public string OriginalInputPath { get; set; } = string.Empty;
    public string OriginalGameDirectory { get; set; } = string.Empty;
    public string GameProfile { get; set; } = string.Empty;
    public string ProfileSelection { get; set; } = string.Empty;
    public string ProfileConfidence { get; set; } = string.Empty;
    public List<string> ProfileReasons { get; set; } = [];
    public string SelectedExecutable { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string LaunchStrategy { get; set; } = string.Empty;
    public string ArgumentList { get; set; } = string.Empty;
    public List<string> EnvironmentAdditions { get; set; } = [];
    public string? SteamAppId { get; set; }
    public string? SteamAppIdSource { get; set; }
    public string? SteamManifestPath { get; set; }
    public string? SteamExecutablePath { get; set; }
    public bool SteamWasRunningAtLaunch { get; set; }
    public bool TempDataSteamDisabled { get; set; }
    public int OriginalSteamAppIdInData { get; set; }
    public bool DebuggerDisabledInTempData { get; set; }
    public long TotalBytesPlanned { get; set; }
    public long TotalBytesCopied { get; set; }
    public double PreparationMilliseconds { get; set; }
    public double CopyMilliseconds { get; set; }
    public List<TempRunCopiedFile> CopiedFiles { get; set; } = [];
    public List<string> ReferencedOriginalDirectories { get; set; } = [];
    public List<int> ProcessIds { get; set; } = [];
    public int? ExitCode { get; set; }
    public string FinalResult { get; set; } = "Pending";
    public string? Error { get; set; }
}

public sealed class TempRunCopiedFile
{
    public string SourcePath { get; set; } = string.Empty;
    public string RelativeDestination { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public double DurationMilliseconds { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed partial class GameProjectSession
{
    private const string TempRunRootFolderName = "SplitGM-VM-Decompiler";
    private const string TempRunChildFolderName = "GameRuns";
    private static readonly JsonSerializerOptions TempRunJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<TempGameRunnerCandidate> DiscoverTempRunRunners(DetectedGameProfile profile)
    {
        ThrowIfDisposed();

        Dictionary<string, TempGameRunnerCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);
        string originalInput = _resolvedInput.OriginalPath;
        string resolvedData = _resolvedInput.DataPath;
        string? originalDirectory = Path.GetDirectoryName(originalInput);
        string? dataDirectory = Path.GetDirectoryName(resolvedData);
        List<string> directories = [.. EnumerateCandidateDirectories(originalDirectory, dataDirectory)];

        if (Path.GetExtension(originalInput).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(originalInput))
        {
            AddCandidate(candidates, originalInput, "The loaded input was a Windows runner executable.", preferred: true);
        }

        foreach (string directory in directories)
        {
            foreach (string executableStem in EnumerateExpectedExecutableStems(profile))
            {
                string candidate = Path.Combine(directory, executableStem + ".exe");
                if (File.Exists(candidate))
                {
                    AddCandidate(
                        candidates,
                        candidate,
                        $"Found expected runner executable {Path.GetFileName(candidate)} beside the game data.",
                        preferred: true);
                }
            }
        }

        foreach (string directory in directories)
        {
            foreach (string executable in SafeEnumerateFiles(directory, "*.exe"))
            {
                string stem = Path.GetFileNameWithoutExtension(executable);
                if (LooksLikeProfileRunner(stem, profile) ||
                    stem.Equals(Path.GetFileNameWithoutExtension(originalInput), StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidate(
                        candidates,
                        executable,
                        $"Executable name matches the effective game profile or original input: {Path.GetFileName(executable)}.",
                        preferred: false);
                }
            }
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.IsPreferred)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<TempGameRunResult> RunGameFromTempAsync(
        DetectedGameProfile profile,
        string runnerExecutablePath,
        IProgress<LogMessage>? log = null,
        CancellationToken cancellationToken = default) =>
        RunGameFromTempAsync(profile, runnerExecutablePath, progress: null, log, cancellationToken);

    public Task<TempGameRunResult> RunGameFromTempAsync(
        DetectedGameProfile profile,
        string runnerExecutablePath,
        IProgress<TempRunProgress>? progress,
        IProgress<LogMessage>? log,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(runnerExecutablePath))
            throw new TempGameRunException("No runner executable was selected.");

        TempRunRequest request = CreateTempRunRequest(profile, runnerExecutablePath);
        TempGameRunService service = new(request, progress, log);
        return service.RunAsync(cancellationToken);
    }

    public static TempGameRunCleanupResult CleanOldTempRunFolders(TimeSpan minimumAge)
    {
        string root = GetTempRunRootDirectory();
        if (!Directory.Exists(root))
            return new TempGameRunCleanupResult(root, 0, 0, []);

        DateTime cutoffUtc = DateTime.UtcNow - minimumAge;
        int removed = 0;
        int skipped = 0;
        List<string> errors = [];

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                DirectoryInfo info = new(directory);
                if (info.CreationTimeUtc > cutoffUtc && info.LastWriteTimeUtc > cutoffUtc)
                {
                    skipped++;
                    continue;
                }

                if (!IsInsideDirectory(root, directory))
                {
                    skipped++;
                    errors.Add($"Skipped unexpected path outside TEMP run root: {directory}");
                    continue;
                }

                if (HasLiveTrackedTempRunProcess(directory, out string liveReason))
                {
                    skipped++;
                    errors.Add(liveReason);
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                skipped++;
                errors.Add($"{directory}: {ex.Message}");
            }
        }

        return new TempGameRunCleanupResult(root, removed, skipped, errors);
    }

    public static string GetTempRunRootDirectory() =>
        Path.Combine(Path.GetTempPath(), TempRunRootFolderName, TempRunChildFolderName);

    private TempRunRequest CreateTempRunRequest(DetectedGameProfile profile, string runnerExecutablePath)
    {
        string[] audioGroupPaths = _data.AudioGroups?
            .Select((group, index) => group?.Path?.Content ?? $"audiogroup{index}.dat")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
            .ToArray() ?? [];

        return new TempRunRequest(
            Profile: profile,
            OriginalInputPath: _resolvedInput.OriginalPath,
            DataSourcePath: _resolvedInput.DataPath,
            RunnerExecutablePath: runnerExecutablePath,
            GameName: Info.GameName,
            DisplayName: Info.DisplayName,
            RunnerExecutableName: Info.RunnerExecutableName,
            SteamAppIdFromData: _data.GeneralInfo?.SteamAppID ?? 0,
            DebuggerWasDisabled: _data.GeneralInfo?.IsDebuggerDisabled ?? true,
            AudioGroupRelativePaths: audioGroupPaths);
    }

    private IEnumerable<string> EnumerateExpectedExecutableStems(DetectedGameProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(Info.RunnerExecutableName))
            yield return Info.RunnerExecutableName;
        if (!string.IsNullOrWhiteSpace(Info.GameName))
            yield return Info.GameName;
        if (!string.IsNullOrWhiteSpace(Info.DisplayName))
            yield return Info.DisplayName;

        if (profile.Profile == GameProfile.Deltarune)
            yield return "DELTARUNE";
        else if (profile.Profile == GameProfile.Undertale)
            yield return "UNDERTALE";
    }

    private sealed record TempRunRequest(
        DetectedGameProfile Profile,
        string OriginalInputPath,
        string DataSourcePath,
        string RunnerExecutablePath,
        string GameName,
        string DisplayName,
        string RunnerExecutableName,
        int SteamAppIdFromData,
        bool DebuggerWasDisabled,
        IReadOnlyList<string> AudioGroupRelativePaths);

    private sealed record TempRunSidecar(
        string Source,
        string RelativeDestination,
        string Reason);

    private sealed record TempRunSteamInfo(
        string? AppId,
        string Source,
        string? ManifestPath,
        string? SteamExecutablePath,
        bool SteamRunning);

    private sealed class TempGameRunService
    {
        private const int CopyBufferSize = 1024 * 1024;
        private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(3);

        private readonly TempRunRequest _request;
        private readonly IProgress<TempRunProgress>? _progress;
        private readonly IProgress<LogMessage>? _log;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly List<string> _diagnosticLines = [];

        private string _runDirectory = string.Empty;
        private string _logFilePath = string.Empty;
        private string _manifestFilePath = string.Empty;

        public TempGameRunService(
            TempRunRequest request,
            IProgress<TempRunProgress>? progress,
            IProgress<LogMessage>? log)
        {
            _request = request;
            _progress = progress;
            _log = log;
        }

        public async Task<TempGameRunResult> RunAsync(CancellationToken cancellationToken)
        {
            string runner = Path.GetFullPath(_request.RunnerExecutablePath);
            if (!File.Exists(runner) || !Path.GetExtension(runner).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new TempGameRunException("The selected runner executable does not exist or is not a Windows .exe file.");

            string dataSource = Path.GetFullPath(_request.DataSourcePath);
            if (!File.Exists(dataSource))
                throw new TempGameRunException("The loaded data source no longer exists, so it cannot be copied for TEMP launch.");

            string? runnerDirectory = Path.GetDirectoryName(runner);
            if (string.IsNullOrWhiteSpace(runnerDirectory) || !Directory.Exists(runnerDirectory))
                throw new TempGameRunException("The selected runner's installation folder could not be identified.");

            _runDirectory = Path.Combine(
                GetTempRunRootDirectory(),
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_runDirectory);
            _logFilePath = Path.Combine(_runDirectory, "SplitGM_TEMP_Run_Log.txt");
            _manifestFilePath = Path.Combine(_runDirectory, "TempRunManifest.json");

            string tempDataPath = Path.Combine(_runDirectory, "data.win");
            string debugOutputPath = Path.ChangeExtension(tempDataPath, ".gamelog.txt");
            TempRunSteamInfo steamInfo = DetectSteamInfo(_request, runnerDirectory);
            IReadOnlyList<TempRunSidecar> sidecars = DiscoverSidecarFiles(_request);
            long dataBytes = new FileInfo(dataSource).Length;
            long sidecarBytes = sidecars.Sum(item => SafeFileLength(item.Source));
            bool writeSteamDisabledTempData = ShouldWriteSteamDisabledTempData(_request, steamInfo);
            TempRunManifest manifest = CreateManifest(dataSource, tempDataPath, runner, runnerDirectory, steamInfo, dataBytes + sidecarBytes, writeSteamDisabledTempData);

            try
            {
                await LogAsync("TEMP_RUN_REQUESTED", "Run Game from TEMP requested.", cancellationToken).ConfigureAwait(false);
                Report(TempRunStage.Requested, "Preparing temporary run...");
                await LogAsync("DATA_SOURCE", dataSource, cancellationToken).ConfigureAwait(false);
                await LogAsync("PROFILE", $"{_request.Profile.DisplayName}; {_request.Profile.SelectionDescription}; {_request.Profile.Confidence}.", cancellationToken).ConfigureAwait(false);
                foreach (string reason in _request.Profile.Reasons)
                    await LogAsync("PROFILE_REASON", reason, cancellationToken).ConfigureAwait(false);

                Report(TempRunStage.DetectingSteam, "Detecting Steam/app ID status...");
                await LogAsync("STEAM_STATUS", $"AppId={steamInfo.AppId ?? "<none>"}; Source={steamInfo.Source}; SteamRunning={steamInfo.SteamRunning}; SteamExe={steamInfo.SteamExecutablePath ?? "<unknown>"}", cancellationToken).ConfigureAwait(false);
                await WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

                Stopwatch preparation = Stopwatch.StartNew();
                Report(TempRunStage.WritingData, "Copying data.win...");
                if (writeSteamDisabledTempData)
                {
                    manifest.TempDataSteamDisabled = true;
                    manifest.OriginalSteamAppIdInData = _request.SteamAppIdFromData;
                    manifest.DebuggerDisabledInTempData = true;
                    await LogAsync("TEMP_DATA_REWRITE", $"Writing TEMP data with Steam disabled and debugger disabled. Original SteamAppID was {_request.SteamAppIdFromData}.", cancellationToken).ConfigureAwait(false);
                    await Task.Run(
                        () => WriteSteamDisabledTempData(dataSource, tempDataPath, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    manifest.TotalBytesCopied += new FileInfo(tempDataPath).Length;
                }
                else
                {
                    await CopyFileAsync(dataSource, tempDataPath, "data.win", "Loaded data file", manifest, cancellationToken).ConfigureAwait(false);
                }

                Stopwatch copy = Stopwatch.StartNew();
                Report(TempRunStage.CopyingSidecars, sidecars.Count == 0 ? "No sidecar files required." : "Copying runtime sidecars...");
                foreach (TempRunSidecar sidecar in sidecars)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = Path.Combine(_runDirectory, sidecar.RelativeDestination);
                    if (!IsInsideDirectory(_runDirectory, destination))
                    {
                        await LogAsync("SIDECAR_SKIPPED", $"Outside TEMP root: {sidecar.RelativeDestination}", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                        continue;

                    await CopyFileAsync(sidecar.Source, destination, sidecar.RelativeDestination, sidecar.Reason, manifest, cancellationToken).ConfigureAwait(false);
                }

                copy.Stop();
                preparation.Stop();
                manifest.CopyMilliseconds = copy.Elapsed.TotalMilliseconds;
                manifest.PreparationMilliseconds = preparation.Elapsed.TotalMilliseconds;
                manifest.PreparedAt = DateTimeOffset.Now;
                manifest.ReferencedOriginalDirectories.Add(runnerDirectory);

                await LogAsync("COPY_COMPLETE", $"Copied {manifest.CopiedFiles.Count:N0} sidecar file(s); {manifest.TotalBytesCopied:N0} byte(s) written to TEMP; copy duration {copy.Elapsed.TotalSeconds:0.00}s.", cancellationToken).ConfigureAwait(false);
                await WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

                Process process = await LaunchAsync(runner, runnerDirectory, tempDataPath, debugOutputPath, manifest, cancellationToken).ConfigureAwait(false);
                int processId = process.Id;
                manifest.ProcessIds.Add(processId);
                manifest.LaunchedAt = DateTimeOffset.Now;
                manifest.FinalResult = "Started";
                await WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

                Process trackedProcess = await ConfirmOrFindRelaunchedProcessAsync(process, runner, manifest, cancellationToken).ConfigureAwait(false);
                if (trackedProcess.Id != processId && !manifest.ProcessIds.Contains(trackedProcess.Id))
                    manifest.ProcessIds.Add(trackedProcess.Id);

                Report(TempRunStage.Started, $"Game started successfully. Process {trackedProcess.Id:N0}.");
                _log?.Report(LogMessage.Success($"TEMP run started from {_runDirectory} with {manifest.LaunchStrategy}."));
                await LogAsync("GAME_STARTED", $"Tracking process {trackedProcess.Id}.", cancellationToken).ConfigureAwait(false);
                await WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

                _ = TrackProcessAsync(trackedProcess, manifest);

                return new TempGameRunResult(
                    _runDirectory,
                    tempDataPath,
                    runner,
                    _logFilePath,
                    _manifestFilePath,
                    runnerDirectory,
                    manifest.LaunchStrategy,
                    trackedProcess.Id,
                    manifest.ProcessIds.ToArray(),
                    manifest.CopiedFiles.Select(file => file.RelativeDestination.Replace('\\', '/')).ToArray(),
                    manifest.SteamAppId,
                    manifest.SteamAppIdSource);
            }
            catch (OperationCanceledException)
            {
                manifest.FinalResult = "Cancelled";
                manifest.Error = "TEMP run preparation was cancelled before the game started.";
                await SafeRecordFailureAsync(manifest, "CANCELLED", manifest.Error).ConfigureAwait(false);
                Report(TempRunStage.Cancelled, "TEMP run preparation cancelled.");
                throw;
            }
            catch (TempGameRunException ex)
            {
                manifest.FinalResult = "Failed";
                manifest.Error = ex.ToString();
                await SafeRecordFailureAsync(manifest, "ERROR", ex.ToString()).ConfigureAwait(false);
                Report(TempRunStage.Failed, "TEMP game launch failed. See the TEMP run log.");
                throw;
            }
            catch (Exception ex) when (ex is not TempGameRunException)
            {
                manifest.FinalResult = "Failed";
                manifest.Error = ex.ToString();
                await SafeRecordFailureAsync(manifest, "ERROR", ex.ToString()).ConfigureAwait(false);
                Report(TempRunStage.Failed, "TEMP game launch failed. See the TEMP run log.");
                throw new TempGameRunException("TEMP game launch failed. See the TEMP run log for details.", ex, _runDirectory, _logFilePath);
            }
        }

        private TempRunManifest CreateManifest(
            string dataSource,
            string tempDataPath,
            string runner,
            string runnerDirectory,
            TempRunSteamInfo steamInfo,
            long totalBytes,
            bool writeSteamDisabledTempData)
        {
            string args = $"-game \"{tempDataPath}\" -debugoutput \"{Path.ChangeExtension(tempDataPath, ".gamelog.txt")}\"";
            return new TempRunManifest
            {
                RequestedAt = DateTimeOffset.Now,
                SourceDataPath = dataSource,
                TempDataPath = tempDataPath,
                OriginalInputPath = _request.OriginalInputPath,
                OriginalGameDirectory = runnerDirectory,
                GameProfile = _request.Profile.DisplayName,
                ProfileSelection = _request.Profile.SelectionDescription,
                ProfileConfidence = _request.Profile.Confidence.ToString(),
                ProfileReasons = [.. _request.Profile.Reasons],
                SelectedExecutable = runner,
                WorkingDirectory = runnerDirectory,
                LaunchStrategy = writeSteamDisabledTempData
                    ? "Direct runner with TEMP data Steam disabled"
                    : "Direct runner",
                ArgumentList = args,
                SteamAppId = steamInfo.AppId,
                SteamAppIdSource = steamInfo.Source,
                SteamManifestPath = steamInfo.ManifestPath,
                SteamExecutablePath = steamInfo.SteamExecutablePath,
                SteamWasRunningAtLaunch = steamInfo.SteamRunning,
                OriginalSteamAppIdInData = _request.SteamAppIdFromData,
                DebuggerDisabledInTempData = _request.DebuggerWasDisabled,
                TotalBytesPlanned = totalBytes
            };
        }

        private async Task<Process> LaunchAsync(
            string runner,
            string workingDirectory,
            string tempDataPath,
            string debugOutputPath,
            TempRunManifest manifest,
            CancellationToken cancellationToken)
        {
            Report(TempRunStage.Launching, $"Launching {_request.Profile.DisplayName} with temporary data...");
            await LogAsync("LAUNCH_STRATEGY", manifest.LaunchStrategy, cancellationToken).ConfigureAwait(false);
            await LogAsync("LAUNCH_RUNNER", runner, cancellationToken).ConfigureAwait(false);
            await LogAsync("LAUNCH_WORKING_DIRECTORY", workingDirectory, cancellationToken).ConfigureAwait(false);
            await LogAsync("LAUNCH_ARGUMENTS", manifest.ArgumentList, cancellationToken).ConfigureAwait(false);

            ProcessStartInfo startInfo = new()
            {
                FileName = runner,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-game");
            startInfo.ArgumentList.Add(tempDataPath);
            startInfo.ArgumentList.Add("-debugoutput");
            startInfo.ArgumentList.Add(debugOutputPath);

            Process process = Process.Start(startInfo)
                ?? throw new TempGameRunException("Windows did not start the selected GameMaker runner.", _runDirectory, _logFilePath);
            await LogAsync("PROCESS_STARTED", $"Process.Start returned PID {process.Id}.", cancellationToken).ConfigureAwait(false);
            return process;
        }

        private async Task<Process> ConfirmOrFindRelaunchedProcessAsync(
            Process process,
            string runner,
            TempRunManifest manifest,
            CancellationToken cancellationToken)
        {
            Report(TempRunStage.WaitingForGameProcess, "Waiting briefly for game process confirmation...");
            DateTime startTime = SafeGetStartTime(process) ?? DateTime.Now;
            try
            {
                Task exitTask = process.WaitForExitAsync(cancellationToken);
                Task delayTask = Task.Delay(ImmediateExitWindow, cancellationToken);
                Task completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
                if (completed != exitTask || !process.HasExited)
                    return process;

                int exitCode = process.ExitCode;
                await LogAsync("PROCESS_EXITED_EARLY", $"PID {process.Id} exited with code {exitCode} inside {ImmediateExitWindow.TotalSeconds:0}s.", cancellationToken).ConfigureAwait(false);
                process.Dispose();

                Process? relaunched = FindRecentMatchingProcess(runner, startTime);
                if (relaunched is not null)
                {
                    await LogAsync("RELAUNCHED_PROCESS_DETECTED", $"Detected replacement game process PID {relaunched.Id}.", cancellationToken).ConfigureAwait(false);
                    return relaunched;
                }

                manifest.ExitCode = exitCode;
                string steamHint = manifest.SteamAppId is null
                    ? string.Empty
                    : $" Steam app ID {manifest.SteamAppId} was detected from {manifest.SteamAppIdSource}, but no replacement game process was found.";
                throw new TempGameRunException(
                    $"The runner exited with code {exitCode} before a game process stayed alive.{steamHint}",
                    _runDirectory,
                    _logFilePath);
            }
            catch (OperationCanceledException)
            {
                process.Dispose();
                throw;
            }
        }

        private async Task TrackProcessAsync(Process process, TempRunManifest manifest)
        {
            try
            {
                await LogAsync("TRACKING_STARTED", $"Background tracking for PID {process.Id}.", CancellationToken.None).ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                manifest.GameExitedAt = DateTimeOffset.Now;
                manifest.ExitCode = process.ExitCode;
                manifest.FinalResult = "Exited";
                await LogAsync("GAME_EXITED", $"PID {process.Id} exited with code {process.ExitCode}.", CancellationToken.None).ConfigureAwait(false);
                await WriteManifestAsync(manifest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync("TRACKING_ERROR", ex.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
            }
        }

        private static Process? FindRecentMatchingProcess(string runner, DateTime startTime)
        {
            string stem = Path.GetFileNameWithoutExtension(runner);
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(stem);
            }
            catch (Exception)
            {
                return null;
            }

            foreach (Process candidate in processes)
            {
                try
                {
                    DateTime candidateStart = candidate.StartTime;
                    if (candidateStart >= startTime.AddSeconds(-1))
                        return candidate;
                }
                catch (Exception)
                {
                    candidate.Dispose();
                }
            }

            return null;
        }

        private async Task CopyFileAsync(
            string source,
            string destination,
            string relativeDestination,
            string reason,
            TempRunManifest manifest,
            CancellationToken cancellationToken)
        {
            Stopwatch fileTimer = Stopwatch.StartNew();
            long bytes = 0;
            DateTimeOffset lastProgress = DateTimeOffset.MinValue;

            await using FileStream input = new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[CopyBufferSize];
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytes += read;
                manifest.TotalBytesCopied += read;

                DateTimeOffset now = DateTimeOffset.Now;
                if (now - lastProgress >= ProgressThrottle)
                {
                    lastProgress = now;
                    Report(TempRunStage.CopyingSidecars, $"Copying {relativeDestination}...", manifest.TotalBytesCopied, manifest.TotalBytesPlanned, relativeDestination);
                }
            }

            fileTimer.Stop();
            manifest.CopiedFiles.Add(new TempRunCopiedFile
            {
                SourcePath = source,
                RelativeDestination = relativeDestination.Replace('\\', '/'),
                Bytes = bytes,
                DurationMilliseconds = fileTimer.Elapsed.TotalMilliseconds,
                Reason = reason
            });

            await LogAsync("FILE_COPIED", $"{relativeDestination.Replace('\\', '/')} | {bytes:N0} bytes | {fileTimer.Elapsed.TotalMilliseconds:0.0} ms | {reason}", cancellationToken).ConfigureAwait(false);
        }

        private static void WriteSteamDisabledTempData(string dataSource, string tempDataPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream input = File.Open(dataSource, FileMode.Open, FileAccess.Read, FileShare.Read);
            using UndertaleData data = UmtNativePipeline.ReadGameData(input);
            cancellationToken.ThrowIfCancellationRequested();

            if (data.GeneralInfo is not null)
            {
                data.GeneralInfo.SteamAppID = 0;
                data.GeneralInfo.Info &= ~UndertaleGeneralInfo.InfoFlags.SteamEnabled;
                data.GeneralInfo.FunctionClassifications &= ~UndertaleGeneralInfo.FunctionClassification.Steam;
                data.GeneralInfo.IsDebuggerDisabled = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(tempDataPath)!);
            using FileStream output = File.Open(tempDataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            UmtNativePipeline.WriteGameData(output, data);
        }

        private async Task LogAsync(string eventName, string message, CancellationToken cancellationToken)
        {
            string line = $"[{DateTimeOffset.Now:O}] +{_elapsed.ElapsedMilliseconds,8:N0}ms {eventName}: {message}";
            _diagnosticLines.Add(line);
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            if (eventName is "TEMP_RUN_REQUESTED" or "STEAM_STATUS" or "TEMP_DATA_REWRITE" or "COPY_COMPLETE" or
                "LAUNCH_STRATEGY" or "PROCESS_STARTED" or "GAME_STARTED" or "PROCESS_EXITED_EARLY" or "ERROR" or "CANCELLED")
            {
                _log?.Report(LogMessage.Info($"{eventName}: {message}"));
            }
        }

        private async Task SafeRecordFailureAsync(TempRunManifest manifest, string eventName, string message)
        {
            try
            {
                await LogAsync(eventName, message, CancellationToken.None).ConfigureAwait(false);
                await WriteManifestAsync(manifest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Report(LogMessage.Error("Failed to write TEMP run failure diagnostics: " + ex.Message));
            }
        }

        private Task WriteManifestAsync(TempRunManifest manifest, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(
                _manifestFilePath,
                JsonSerializer.Serialize(manifest, TempRunJsonOptions),
                new UTF8Encoding(false),
                cancellationToken);

        private void Report(
            TempRunStage stage,
            string message,
            long bytesCompleted = 0,
            long bytesTotal = 0,
            string? relativePath = null) =>
            _progress?.Report(new TempRunProgress(stage, message, bytesCompleted, bytesTotal, relativePath));
    }

    private static IReadOnlyList<TempRunSidecar> DiscoverSidecarFiles(TempRunRequest request)
    {
        Dictionary<string, TempRunSidecar> sidecars = new(StringComparer.OrdinalIgnoreCase);
        string? originalDirectory = Path.GetDirectoryName(request.OriginalInputPath);
        string? dataDirectory = Path.GetDirectoryName(request.DataSourcePath);
        string[] sourceDirectories = [.. EnumerateCandidateDirectories(originalDirectory, dataDirectory)];

        foreach (string directory in sourceDirectories)
        {
            foreach (string file in SafeEnumerateFiles(directory, "audiogroup*.dat"))
                AddSidecar(sidecars, directory, file, "GameMaker audio group data file");

            foreach (string fileName in new[] { "options.ini", "steam_appid.txt", "steam_api.dll", "steam_api64.dll" })
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    AddSidecar(sidecars, directory, path, "Known top-level GameMaker/Steam runtime sidecar");
            }

            foreach (string relativeAudioGroup in request.AudioGroupRelativePaths)
            {
                string? source = TryCombineInside(directory, relativeAudioGroup);
                if (source is not null && File.Exists(source))
                    AddSidecar(sidecars, directory, source, "Audio group path recorded in data.win");
            }

            AddProfileSidecars(sidecars, directory, request.Profile);
        }

        return sidecars.Values
            .OrderBy(sidecar => sidecar.RelativeDestination, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddProfileSidecars(
        IDictionary<string, TempRunSidecar> sidecars,
        string directory,
        DetectedGameProfile profile)
    {
        if (profile.Profile == GameProfile.Undertale)
        {
            foreach (string file in SafeEnumerateFiles(directory, "*.ogg"))
                AddSidecar(sidecars, directory, file, "UNDERTALE external OGG audio");

            foreach (string fileName in new[] { "D3DX9_43.dll", "splash.png" })
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    AddSidecar(sidecars, directory, path, "UNDERTALE runtime sidecar");
            }

            return;
        }

        if (profile.Profile != GameProfile.Deltarune)
            return;

        foreach (string relativeDirectory in new[] { "lang", "mus" })
            AddDirectorySidecars(sidecars, directory, relativeDirectory, "DELTARUNE shared runtime data");
    }

    private static void AddDirectorySidecars(
        IDictionary<string, TempRunSidecar> sidecars,
        string rootDirectory,
        string relativeDirectory,
        string reason)
    {
        string? directory = TryCombineInside(rootDirectory, relativeDirectory);
        if (directory is null || !Directory.Exists(directory))
            return;

        foreach (string file in SafeEnumerateFiles(directory, "*", SearchOption.AllDirectories))
            AddSidecar(sidecars, rootDirectory, file, reason);
    }

    private static TempRunSteamInfo DetectSteamInfo(TempRunRequest request, string runnerDirectory)
    {
        string? steamExe = FindSteamExecutable();
        bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
        string? dataSteamAppId = NormalizeSteamAppId(request.SteamAppIdFromData);

        string appIdFile = Path.Combine(runnerDirectory, "steam_appid.txt");
        if (File.Exists(appIdFile))
        {
            string? appId = File.ReadLines(appIdFile)
                .Select(line => line.Trim())
                .FirstOrDefault(line => Regex.IsMatch(line, @"^\d+$"));
            if (!string.IsNullOrWhiteSpace(appId))
                return new TempRunSteamInfo(appId, "steam_appid.txt beside runner", null, steamExe, steamRunning);
        }

        if (dataSteamAppId is not null)
            return new TempRunSteamInfo(dataSteamAppId, "GEN8 SteamAppID in loaded data.win", null, steamExe, steamRunning);

        (string? ManifestAppId, string? ManifestPath) manifest = DetectAppIdFromSteamManifest(runnerDirectory);
        if (!string.IsNullOrWhiteSpace(manifest.ManifestAppId))
            return new TempRunSteamInfo(manifest.ManifestAppId, "Steam appmanifest matching install directory", manifest.ManifestPath, steamExe, steamRunning);

        if (request.Profile.Profile == GameProfile.Undertale)
            return new TempRunSteamInfo("391540", "UNDERTALE profile fallback", null, steamExe, steamRunning);
        if (request.Profile.Profile == GameProfile.Deltarune)
            return new TempRunSteamInfo("1671210", "DELTARUNE profile fallback", null, steamExe, steamRunning);

        return new TempRunSteamInfo(null, "No Steam app ID detected", null, steamExe, steamRunning);
    }

    private static bool ShouldWriteSteamDisabledTempData(TempRunRequest request, TempRunSteamInfo steamInfo) =>
        request.SteamAppIdFromData != 0 ||
        !string.IsNullOrWhiteSpace(steamInfo.AppId);

    private static string? NormalizeSteamAppId(int steamAppId)
    {
        if (steamAppId == 0)
            return null;

        long normalized = Math.Abs((long)steamAppId);
        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static (string? AppId, string? ManifestPath) DetectAppIdFromSteamManifest(string runnerDirectory)
    {
        DirectoryInfo? current = new(runnerDirectory);
        while (current?.Parent is not null)
        {
            if (current.Parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                current.Parent.Parent is { Name: "steamapps" } steamApps)
            {
                string installDir = current.Name;
                foreach (string manifest in SafeEnumerateFiles(steamApps.FullName, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(manifest);
                    string? appId = ReadAcfValue(text, "appid");
                    string? manifestInstallDir = ReadAcfValue(text, "installdir");
                    if (appId is not null &&
                        manifestInstallDir is not null &&
                        manifestInstallDir.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return (appId, manifest);
                    }
                }
            }

            current = current.Parent;
        }

        return (null, null);
    }

    private static string? ReadAcfValue(string text, string key)
    {
        Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindSteamExecutable()
    {
        foreach (Process process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                string? path = SafeGetProcessPath(process);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? SafeGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? SafeGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasLiveTrackedTempRunProcess(string directory, out string reason)
    {
        reason = string.Empty;
        string manifestPath = Path.Combine(directory, "TempRunManifest.json");
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            TempRunManifest? manifest = JsonSerializer.Deserialize<TempRunManifest>(File.ReadAllText(manifestPath), TempRunJsonOptions);
            if (manifest is null)
                return false;

            foreach (int processId in manifest.ProcessIds)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        reason = $"Skipped active TEMP run {directory}; tracked process {processId} is still running.";
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Process no longer exists.
                }
                catch (InvalidOperationException)
                {
                    // Process exited while being queried.
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"Skipped TEMP run {directory}; manifest could not be inspected safely: {ex.Message}";
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(params string?[] directories)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            string full = Path.GetFullPath(directory);
            if (seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        string directory,
        string searchPattern,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        try
        {
            return Directory.EnumerateFiles(directory, searchPattern, searchOption).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool LooksLikeProfileRunner(string executableStem, DetectedGameProfile profile)
    {
        if (profile.Profile == GameProfile.Deltarune)
            return executableStem.Contains("deltarune", StringComparison.OrdinalIgnoreCase);
        if (profile.Profile == GameProfile.Undertale)
            return executableStem.Contains("undertale", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static void AddCandidate(
        IDictionary<string, TempGameRunnerCandidate> candidates,
        string path,
        string reason,
        bool preferred)
    {
        string full = Path.GetFullPath(path);
        if (!candidates.TryGetValue(full, out TempGameRunnerCandidate? existing) || (preferred && !existing.IsPreferred))
            candidates[full] = new TempGameRunnerCandidate(full, reason, preferred);
    }

    private static void AddSidecar(
        IDictionary<string, TempRunSidecar> sidecars,
        string rootDirectory,
        string sourcePath,
        string reason)
    {
        string fullRoot = Path.GetFullPath(rootDirectory);
        string fullSource = Path.GetFullPath(sourcePath);
        if (!IsInsideDirectory(fullRoot, fullSource))
            return;

        string relative = Path.GetRelativePath(fullRoot, fullSource);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return;

        sidecars.TryAdd(relative, new TempRunSidecar(fullSource, relative, reason));
    }

    private static string? TryCombineInside(string rootDirectory, string relativePath)
    {
        string root = Path.GetFullPath(rootDirectory);
        string combined = Path.GetFullPath(Path.Combine(root, relativePath));
        return IsInsideDirectory(root, combined) ? combined : null;
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsInsideDirectory(string rootDirectory, string candidatePath)
    {
        string root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
