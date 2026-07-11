using UndertaleModLib;
using UndertaleModLib.Models;

namespace SplitGM.Core;

public sealed class GameProjectLoader
{
    public Task<GameProjectSession> LoadAsync(
        string inputPath,
        IProgress<DecompileProgress>? progress = null,
        IProgress<LogMessage>? log = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => LoadCore(inputPath, progress, log, cancellationToken), cancellationToken);
    }

    private static GameProjectSession LoadCore(
        string inputPath,
        IProgress<DecompileProgress>? progress,
        IProgress<LogMessage>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DecompileProgress(
            DecompileStage.ResolvingInput,
            0,
            0,
            "Resolving the selected GameMaker file..."));

        ResolvedGameInput? resolved = null;
        UndertaleData? data = null;

        try
        {
            resolved = InputResolver.Resolve(
                inputPath,
                message => log?.Report(message),
                cancellationToken);

            progress?.Report(new DecompileProgress(
                DecompileStage.LoadingData,
                0,
                0,
                "Loading and decoding GameMaker data..."));

            List<string> warnings = [];
            using (FileStream input = File.Open(
                       resolved.DataPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                data = UndertaleIO.Read(
                    input,
                    warningHandler: (message, important) =>
                    {
                        warnings.Add(message);
                        log?.Report(LogMessage.Warning(message));
                    },
                    messageHandler: message => log?.Report(LogMessage.Info(message)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DecompileProgress(
                DecompileStage.InspectingGame,
                0,
                0,
                "Inspecting runtime, bytecode, and resource information..."));

            UndertaleGeneralInfo? generalInfo = data.GeneralInfo;
            string gameName = generalInfo?.Name?.Content ?? "Unknown Game";
            string displayName = generalInfo?.DisplayName?.Content ?? gameName;
            string version = generalInfo is null
                ? "Unknown"
                : $"{generalInfo.Major}.{generalInfo.Minor}.{generalInfo.Release}.{generalInfo.Build}";
            int bytecodeVersion = generalInfo?.BytecodeVersion ?? 0;
            bool isYyc = data.IsYYC();
            bool unsupportedBytecode = data.UnsupportedBytecodeVersion;
            int rootCodeCount = data.Code?.Count(code => code is not null && code.ParentEntry is null) ?? 0;

            (GameCompatibility compatibility, string compatibilityMessage) = DetermineCompatibility(
                isYyc,
                unsupportedBytecode,
                rootCodeCount);

            GameProjectInfo info = new(
                OriginalInput: resolved.OriginalPath,
                ResolvedDataSource: resolved.DeleteOnDispose
                    ? "Temporary embedded archive"
                    : resolved.DataPath,
                ResolutionMethod: resolved.ResolutionMethod,
                GameName: gameName,
                DisplayName: displayName,
                GameMakerVersion: version,
                BytecodeVersion: bytecodeVersion,
                RuntimeType: isYyc ? "YYC" : "VM",
                IsYyc: isYyc,
                UnsupportedBytecodeVersion: unsupportedBytecode,
                Compatibility: compatibility,
                CompatibilityMessage: compatibilityMessage,
                InputFileSize: new FileInfo(resolved.DataPath).Length,
                LoadedAt: DateTimeOffset.Now);

            ResourceCounts counts = new(
                RootCodeEntries: rootCodeCount,
                Scripts: data.Scripts?.Count ?? 0,
                Objects: data.GameObjects?.Count ?? 0,
                Rooms: data.Rooms?.Count ?? 0,
                Sprites: data.Sprites?.Count ?? 0,
                Sounds: data.Sounds?.Count ?? 0,
                Fonts: data.Fonts?.Count ?? 0,
                Shaders: data.Shaders?.Count ?? 0,
                Backgrounds: data.Backgrounds?.Count ?? 0,
                Paths: data.Paths?.Count ?? 0,
                Timelines: data.Timelines?.Count ?? 0,
                Extensions: data.Extensions?.Count ?? 0,
                AudioGroups: data.AudioGroups?.Count ?? 0,
                Sequences: data.Sequences?.Count ?? 0,
                AnimationCurves: data.AnimationCurves?.Count ?? 0,
                ParticleSystems: data.ParticleSystems?.Count ?? 0,
                ParticleSystemEmitters: data.ParticleSystemEmitters?.Count ?? 0,
                TextureGroups: data.TextureGroupInfo?.Count ?? 0,
                TexturePageItems: data.TexturePageItems?.Count ?? 0,
                TexturePages: data.EmbeddedTextures?.Count ?? 0,
                EmbeddedImages: data.EmbeddedImages?.Count ?? 0,
                EmbeddedAudio: data.EmbeddedAudio?.Count ?? 0,
                FilterEffects: data.FilterEffects?.Count ?? 0,
                Strings: data.Strings?.Count ?? 0,
                Functions: data.Functions?.Count ?? 0,
                Variables: data.Variables?.Count ?? 0);

            log?.Report(LogMessage.Info($"Game: {displayName}"));
            log?.Report(LogMessage.Info(
                $"GameMaker {version}; bytecode {bytecodeVersion}; runtime {info.RuntimeType}."));
            log?.Report(LogMessage.Info(
                $"Resources: {counts.RootCodeEntries:N0} code entries, {counts.Objects:N0} objects, " +
                $"{counts.Rooms:N0} rooms, {counts.Sprites:N0} sprites."));

            if (compatibility == GameCompatibility.Compatible)
                log?.Report(LogMessage.Success(compatibilityMessage));
            else
                log?.Report(LogMessage.Warning(compatibilityMessage));

            progress?.Report(new DecompileProgress(
                DecompileStage.BuildingResourceIndex,
                1,
                1,
                "Building SplitGM resource indexes..."));

            GameProjectSession session = new(resolved, data, info, counts, warnings);
            resolved = null;
            data = null;
            return session;
        }
        catch
        {
            data?.Dispose();
            resolved?.Dispose();
            throw;
        }
    }

    private static (GameCompatibility Compatibility, string Message) DetermineCompatibility(
        bool isYyc,
        bool unsupportedBytecode,
        int rootCodeCount)
    {
        if (isYyc)
        {
            return (
                GameCompatibility.YycNoVmCode,
                "YYC native code was detected. Resources can be inspected, but high-level GML is not present in the data file.");
        }

        if (unsupportedBytecode)
        {
            return (
                GameCompatibility.UnsupportedBytecode,
                "The data file uses a bytecode version marked unsupported by UndertaleModLib. Resource browsing may work, but code results may be incomplete.");
        }

        if (rootCodeCount == 0)
        {
            return (
                GameCompatibility.NoCodeEntries,
                "No root VM code entries were found. This may be an auxiliary data or audio-group file rather than the main game archive.");
        }

        return (
            GameCompatibility.Compatible,
            "Compatible GameMaker VM data detected. Resource inspection, GML decompilation, assembly viewing, searching, and export are available.");
    }
}
