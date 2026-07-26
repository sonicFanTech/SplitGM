#nullable enable

using System.IO;

namespace SGMVMDLauncher;

internal enum StartupDisplayMode
{
    Normal,
    FirstHalf,
    SecondHalf,
    FirstHalfStatic,
    SecondHalfStatic
}

/// <summary>
/// Reads only the launcher-facing [Startup] section of SplitGM_Settings.ini.
/// SplitGM.Gui remains the owner/editor of the settings file.
/// </summary>
internal static class StartupDisplaySettings
{
    public static StartupDisplayMode Load()
    {
        foreach (string path in GetCandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                string section = string.Empty;
                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                        continue;

                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        section = line[1..^1].Trim();
                        continue;
                    }

                    if (!section.Equals("Startup", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    string key = line[..equals].Trim();
                    if (!key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return ParseMode(line[(equals + 1)..].Trim());
                }
            }
            catch
            {
                // A damaged or temporarily locked settings file must never stop
                // SplitGM from starting. Use the full animation as the safe default.
            }
        }

        return StartupDisplayMode.Normal;
    }

    private static StartupDisplayMode ParseMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "firsthalf" => StartupDisplayMode.FirstHalf,
            "secondhalf" => StartupDisplayMode.SecondHalf,
            "firsthalfstatic" => StartupDisplayMode.FirstHalfStatic,
            "secondhalfstatic" => StartupDisplayMode.SecondHalfStatic,
            _ => StartupDisplayMode.Normal
        };

    private static IEnumerable<string> GetCandidatePaths()
    {
        // In release builds the launcher and SplitGM executable share this folder.
        yield return Path.Combine(AppContext.BaseDirectory, "SplitGM_Settings.ini");

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SplitGM-VM-Decompiler",
            "SplitGM_Settings.ini");

        if (!string.Equals(
                fallback,
                Path.Combine(AppContext.BaseDirectory, "SplitGM_Settings.ini"),
                StringComparison.OrdinalIgnoreCase))
        {
            yield return fallback;
        }
    }
}
