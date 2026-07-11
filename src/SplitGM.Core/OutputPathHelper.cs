using System.Text;

namespace SplitGM.Core;

public static class OutputPathHelper
{
    private static readonly string[] ObjectEventMarkers =
    [
        "Create_", "Destroy_", "Alarm_", "Step_", "Collision_", "Keyboard_",
        "Mouse_", "Other_", "Draw_", "KeyPress_", "KeyRelease_", "Gesture_",
        "PreCreate_", "Cleanup_"
    ];

    public static CodeCategory CategoryForCodeName(string name)
    {
        if (name.StartsWith("gml_Script_", StringComparison.OrdinalIgnoreCase))
            return CodeCategory.Scripts;
        if (name.StartsWith("gml_Object_", StringComparison.OrdinalIgnoreCase))
            return CodeCategory.ObjectEvents;
        if (name.StartsWith("gml_Room", StringComparison.OrdinalIgnoreCase))
            return CodeCategory.RoomCode;
        if (name.StartsWith("gml_GlobalScript_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("gml_GlobalInit_", StringComparison.OrdinalIgnoreCase))
            return CodeCategory.GlobalInit;
        if (name.StartsWith("gml_Timeline_", StringComparison.OrdinalIgnoreCase))
            return CodeCategory.Timelines;
        return CodeCategory.Other;
    }

    public static string BuildRelativeGmlPath(
        CodeEntryInfo entry,
        IReadOnlyList<string> objectNames,
        IReadOnlyList<string> roomNames)
    {
        string name = entry.Name;
        return entry.Category switch
        {
            CodeCategory.Scripts => BuildScriptPath(name),
            CodeCategory.ObjectEvents => BuildObjectPath(name, objectNames),
            CodeCategory.RoomCode => BuildRoomPath(name, roomNames),
            CodeCategory.GlobalInit => Path.Combine(
                "GlobalInit",
                SafeFileName(StripFirstMatchingPrefix(name, "gml_GlobalScript_", "gml_GlobalInit_")) + ".gml"),
            CodeCategory.Timelines => Path.Combine(
                "Timelines",
                SafeFileName(StripFirstMatchingPrefix(name, "gml_Timeline_")) + ".gml"),
            _ => Path.Combine("Code", "Other", SafeFileName(name) + ".gml")
        };
    }

    public static string SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unnamed";

        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        StringBuilder result = new(value.Length);
        foreach (char character in value)
            result.Append(invalid.Contains(character) ? '_' : character);

        string sanitized = result.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "unnamed";

        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
            "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        if (reserved.Contains(sanitized, StringComparer.OrdinalIgnoreCase))
            sanitized = "_" + sanitized;

        return sanitized.Length > 180 ? sanitized[..180] : sanitized;
    }

    public static string EnsureUniquePath(string fullPath)
    {
        if (!File.Exists(fullPath))
            return fullPath;

        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        string extension = Path.GetExtension(fullPath);
        int suffix = 2;

        while (true)
        {
            string candidate = Path.Combine(directory, $"{fileName}_{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
            suffix++;
        }
    }

    private static string BuildScriptPath(string name)
    {
        string assetName = StripFirstMatchingPrefix(name, "gml_Script_");
        string safe = SafeFileName(assetName);
        return Path.Combine("Scripts", safe, safe + ".gml");
    }

    private static string BuildObjectPath(string name, IReadOnlyList<string> objectNames)
    {
        string remainder = StripFirstMatchingPrefix(name, "gml_Object_");
        string? objectName = FindLongestResourcePrefix(remainder, objectNames);
        if (objectName is null)
            return Path.Combine("Objects", "Unresolved", SafeFileName(remainder) + ".gml");

        string eventName = remainder.Length > objectName.Length
            ? remainder[(objectName.Length + 1)..]
            : "UnknownEvent";

        if (!ObjectEventMarkers.Any(marker => eventName.StartsWith(marker, StringComparison.OrdinalIgnoreCase)))
            eventName = "Event_" + eventName;

        return Path.Combine(
            "Objects",
            SafeFileName(objectName),
            SafeFileName(eventName) + ".gml");
    }

    private static string BuildRoomPath(string name, IReadOnlyList<string> roomNames)
    {
        string remainder = StripFirstMatchingPrefix(
            name,
            "gml_RoomCC_",
            "gml_RoomCode_",
            "gml_Room_");

        string? roomName = FindLongestResourcePrefix(remainder, roomNames);
        if (roomName is null)
            return Path.Combine("Rooms", "Unresolved", SafeFileName(remainder) + ".gml");

        string codeName = remainder.Length > roomName.Length
            ? remainder[(roomName.Length + 1)..]
            : "CreationCode";

        return Path.Combine(
            "Rooms",
            SafeFileName(roomName),
            SafeFileName(codeName) + ".gml");
    }

    private static string? FindLongestResourcePrefix(string remainder, IReadOnlyList<string> names)
    {
        return names
            .Where(name =>
                remainder.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                remainder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name.Length)
            .FirstOrDefault();
    }

    private static string StripFirstMatchingPrefix(string value, params string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..];
        }
        return value;
    }
}
