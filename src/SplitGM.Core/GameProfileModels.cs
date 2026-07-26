namespace SplitGM.Core;

public enum GameProfilePreference
{
    AutoDetect,
    Generic,
    Undertale,
    Deltarune
}

public enum GameProfile
{
    Generic,
    Undertale,
    Deltarune
}

public enum GameProfileConfidence
{
    Low,
    Medium,
    High
}

public sealed record DetectedGameProfile(
    GameProfile Profile,
    GameProfileConfidence Confidence,
    IReadOnlyList<string> Reasons,
    bool ManuallyOverridden = false,
    GameProfilePreference Preference = GameProfilePreference.AutoDetect)
{
    public string DisplayName => GameProfileSupport.GetDisplayName(Profile);

    public string SelectionDescription => ManuallyOverridden
        ? "Manual override"
        : "Auto-detected";
}

public static class GameProfileSupport
{
    public static string GetDisplayName(GameProfile profile) => profile switch
    {
        GameProfile.Undertale => "UNDERTALE",
        GameProfile.Deltarune => "DELTARUNE",
        _ => "Generic GameMaker Game"
    };

    public static string GetPreferenceDisplayName(GameProfilePreference preference) => preference switch
    {
        GameProfilePreference.AutoDetect => "Auto Detect",
        GameProfilePreference.Generic => "Generic GameMaker Game",
        GameProfilePreference.Undertale => "UNDERTALE",
        GameProfilePreference.Deltarune => "DELTARUNE",
        _ => "Auto Detect"
    };

    public static GameProfilePreference ParsePreference(string? value) =>
        value?.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty) switch
        {
            "generic" or "genericgamemakergame" => GameProfilePreference.Generic,
            "undertale" => GameProfilePreference.Undertale,
            "deltarune" => GameProfilePreference.Deltarune,
            _ => GameProfilePreference.AutoDetect
        };

    public static string NormalizePreference(GameProfilePreference preference) => preference switch
    {
        GameProfilePreference.Generic => nameof(GameProfilePreference.Generic),
        GameProfilePreference.Undertale => nameof(GameProfilePreference.Undertale),
        GameProfilePreference.Deltarune => nameof(GameProfilePreference.Deltarune),
        _ => nameof(GameProfilePreference.AutoDetect)
    };

    public static DetectedGameProfile ApplyPreference(
        DetectedGameProfile detected,
        GameProfilePreference preference)
    {
        if (preference == GameProfilePreference.AutoDetect)
            return detected with { Preference = preference, ManuallyOverridden = false };

        GameProfile profile = preference switch
        {
            GameProfilePreference.Undertale => GameProfile.Undertale,
            GameProfilePreference.Deltarune => GameProfile.Deltarune,
            _ => GameProfile.Generic
        };

        List<string> reasons =
        [
            $"Manual profile selected in SplitGM settings: {GetPreferenceDisplayName(preference)}."
        ];
        if (detected.Profile != profile)
        {
            reasons.Add(
                $"Auto-detection suggested {detected.DisplayName} with {detected.Confidence} confidence.");
        }

        return new DetectedGameProfile(
            profile,
            GameProfileConfidence.High,
            reasons,
            ManuallyOverridden: true,
            Preference: preference);
    }
}
