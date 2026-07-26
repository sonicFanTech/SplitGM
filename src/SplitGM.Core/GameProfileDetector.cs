using UndertaleModLib;
using UndertaleModLib.Models;

namespace SplitGM.Core;

internal static class GameProfileDetector
{
    public static DetectedGameProfile Detect(
        UndertaleData data,
        string originalInput,
        string resolvedDataSource)
    {
        Dictionary<GameProfile, int> scores = new()
        {
            [GameProfile.Undertale] = 0,
            [GameProfile.Deltarune] = 0
        };
        Dictionary<GameProfile, List<string>> reasons = new()
        {
            [GameProfile.Undertale] = [],
            [GameProfile.Deltarune] = []
        };

        string gameName = data.GeneralInfo?.Name?.Content ?? string.Empty;
        string displayName = data.GeneralInfo?.DisplayName?.Content ?? string.Empty;
        string runnerName = data.GeneralInfo?.FileName?.Content ?? string.Empty;
        string pathText = string.Join(
            " ",
            originalInput,
            resolvedDataSource,
            Path.GetDirectoryName(originalInput) ?? string.Empty,
            Path.GetDirectoryName(resolvedDataSource) ?? string.Empty);

        AddTextSignal(scores, reasons, GameProfile.Deltarune, pathText, "deltarune", 4, "Input path or containing directory mentions DELTARUNE.");
        AddTextSignal(scores, reasons, GameProfile.Undertale, pathText, "undertale", 4, "Input path or containing directory mentions UNDERTALE.");
        AddTextSignal(scores, reasons, GameProfile.Deltarune, gameName + " " + displayName + " " + runnerName, "deltarune", 5, "Internal game, display, or runner name mentions DELTARUNE.");
        AddTextSignal(scores, reasons, GameProfile.Undertale, gameName + " " + displayName + " " + runnerName, "undertale", 5, "Internal game, display, or runner name mentions UNDERTALE.");

        HashSet<string> names = CollectResourceNames(data);
        AddNameSignals(scores, reasons, GameProfile.Deltarune, names, 2,
            "DELTARUNE-specific resource names were found.",
            "obj_CHAPTER_SELECT",
            "obj_darkcontroller",
            "obj_writer",
            "PLACE_CHAPTER_SELECT",
            "PLACE_CHAPTER_SELECT_2x",
            "room_dw_castle",
            "scr_dark_marker");
        AddNameSignals(scores, reasons, GameProfile.Undertale, names, 2,
            "UNDERTALE-specific resource names were found.",
            "obj_sans",
            "obj_papyrus",
            "obj_flowey",
            "obj_heart",
            "room_introstory",
            "room_fire1",
            "scr_monsterdefeat");

        AddNameCombination(scores, reasons, GameProfile.Deltarune, names, 3,
            "DELTARUNE chapter-selection object/room combination was found.",
            "obj_CHAPTER_SELECT",
            "PLACE_CHAPTER_SELECT");
        AddNameCombination(scores, reasons, GameProfile.Undertale, names, 3,
            "UNDERTALE character/controller resource combination was found.",
            "obj_sans",
            "obj_papyrus",
            "obj_flowey");

        GameProfile bestProfile = scores[GameProfile.Deltarune] >= scores[GameProfile.Undertale]
            ? GameProfile.Deltarune
            : GameProfile.Undertale;
        GameProfile otherProfile = bestProfile == GameProfile.Deltarune
            ? GameProfile.Undertale
            : GameProfile.Deltarune;
        int bestScore = scores[bestProfile];
        int otherScore = scores[otherProfile];

        if (bestScore < 4 || bestScore - otherScore < 2)
        {
            List<string> genericReasons =
            [
                bestScore == 0
                    ? "No UNDERTALE or DELTARUNE metadata/resource signals were strong enough."
                    : $"No profile exceeded the Generic fallback threshold decisively. Top score was {bestScore}, next score was {otherScore}."
            ];
            return new DetectedGameProfile(
                GameProfile.Generic,
                GameProfileConfidence.Low,
                genericReasons);
        }

        GameProfileConfidence confidence = bestScore >= 8 && bestScore - otherScore >= 3
            ? GameProfileConfidence.High
            : GameProfileConfidence.Medium;
        return new DetectedGameProfile(bestProfile, confidence, reasons[bestProfile]);
    }

    private static void AddTextSignal(
        IDictionary<GameProfile, int> scores,
        IDictionary<GameProfile, List<string>> reasons,
        GameProfile profile,
        string text,
        string needle,
        int score,
        string reason)
    {
        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            scores[profile] += score;
            reasons[profile].Add(reason);
        }
    }

    private static void AddNameSignals(
        IDictionary<GameProfile, int> scores,
        IDictionary<GameProfile, List<string>> reasons,
        GameProfile profile,
        ISet<string> names,
        int scorePerHit,
        string reason,
        params string[] signals)
    {
        int hits = signals.Count(name => names.Contains(name));
        if (hits <= 0)
            return;

        scores[profile] += hits * scorePerHit;
        reasons[profile].Add($"{reason} Hits: {hits:N0}.");
    }

    private static void AddNameCombination(
        IDictionary<GameProfile, int> scores,
        IDictionary<GameProfile, List<string>> reasons,
        GameProfile profile,
        ISet<string> names,
        int score,
        string reason,
        params string[] requiredNames)
    {
        if (!requiredNames.All(names.Contains))
            return;

        scores[profile] += score;
        reasons[profile].Add(reason);
    }

    private static HashSet<string> CollectResourceNames(UndertaleData data)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        AddNames(names, data.GameObjects);
        AddNames(names, data.Rooms);
        AddNames(names, data.Scripts);
        AddNames(names, data.Sprites);
        AddNames(names, data.Sounds);
        AddNames(names, data.Backgrounds);
        AddNames(names, data.Paths);
        AddNames(names, data.Timelines);
        AddNames(names, data.Extensions);
        AddNames(names, data.Functions);
        AddNames(names, data.Variables);
        AddNames(names, data.Code);
        return names;
    }

    private static void AddNames(HashSet<string> names, IEnumerable<UndertaleNamedResource?>? resources)
    {
        if (resources is null)
            return;

        foreach (UndertaleNamedResource? resource in resources)
        {
            string? name = resource?.Name?.Content;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
    }
}
