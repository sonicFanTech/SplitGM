#nullable enable

using System.Globalization;
using System.IO;
using System.Text;

namespace SplitGM.Gui;

public sealed class SplitGmSettings
{
    public bool ExportResources { get; set; } = true;
    public bool ExportAssembly { get; set; } = true;
    public bool ExportIndexes { get; set; } = true;
    public bool ConfirmOverwrite { get; set; } = true;
    public bool OpenOutputAfterExport { get; set; }
    public bool ShowOperationWindow { get; set; } = true;
    public bool AutoCloseOperationWindow { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool RememberWindowPlacement { get; set; } = true;
    public int RelationshipResultLimit { get; set; } = 1000;
    public string DefaultExportDirectory { get; set; } = string.Empty;
    public string LastOpenedGame { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1460;
    public double WindowHeight { get; set; } = 900;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    public string SettingsPath { get; private set; } = GetPreferredPath();

    public static SplitGmSettings Load()
    {
        SplitGmSettings settings = new();
        string preferred = GetPreferredPath();
        string fallback = GetFallbackPath();
        string path = File.Exists(preferred) ? preferred : fallback;
        settings.SettingsPath = preferred;

        if (!File.Exists(path))
            return settings;

        try
        {
            string section = string.Empty;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line[..equals].Trim();
                string value = Unescape(line[(equals + 1)..].Trim());
                settings.ApplyValue(section, key, value);
            }
            settings.SettingsPath = path;
        }
        catch
        {
            // Invalid individual settings are ignored; SplitGM keeps safe defaults.
        }
        return settings;
    }

    public void Save()
    {
        string preferred = GetPreferredPath();
        string fallback = GetFallbackPath();
        string content = BuildIni();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
            File.WriteAllText(preferred, content, new UTF8Encoding(false));
            SettingsPath = preferred;
        }
        catch
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            File.WriteAllText(fallback, content, new UTF8Encoding(false));
            SettingsPath = fallback;
        }
    }

    private string BuildIni()
    {
        StringBuilder output = new();
        output.AppendLine("; SplitGM-VM Decompiler settings");
        output.AppendLine("; This file is written by SplitGM. Boolean values are true or false.");
        output.AppendLine();
        output.AppendLine("[General]");
        output.AppendLine($"ConfirmOverwrite={ConfirmOverwrite.ToString().ToLowerInvariant()}");
        output.AppendLine($"OpenOutputAfterExport={OpenOutputAfterExport.ToString().ToLowerInvariant()}");
        output.AppendLine($"ShowOperationWindow={ShowOperationWindow.ToString().ToLowerInvariant()}");
        output.AppendLine($"AutoCloseOperationWindow={AutoCloseOperationWindow.ToString().ToLowerInvariant()}");
        output.AppendLine($"RelationshipResultLimit={Math.Clamp(RelationshipResultLimit, 100, 10000)}");
        output.AppendLine($"DefaultExportDirectory={Escape(DefaultExportDirectory)}");
        output.AppendLine($"LastOpenedGame={Escape(LastOpenedGame)}");
        output.AppendLine();
        output.AppendLine("[Display]");
        output.AppendLine($"ShowLineNumbers={ShowLineNumbers.ToString().ToLowerInvariant()}");
        output.AppendLine($"WordWrap={WordWrap.ToString().ToLowerInvariant()}");
        output.AppendLine($"RememberWindowPlacement={RememberWindowPlacement.ToString().ToLowerInvariant()}");
        output.AppendLine();
        output.AppendLine("[Export]");
        output.AppendLine($"ExportResources={ExportResources.ToString().ToLowerInvariant()}");
        output.AppendLine($"ExportAssembly={ExportAssembly.ToString().ToLowerInvariant()}");
        output.AppendLine($"ExportIndexes={ExportIndexes.ToString().ToLowerInvariant()}");
        output.AppendLine();
        output.AppendLine("[Window]");
        output.AppendLine($"Width={WindowWidth.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Height={WindowHeight.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Left={WindowLeft.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Top={WindowTop.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Maximized={WindowMaximized.ToString().ToLowerInvariant()}");
        return output.ToString();
    }

    private void ApplyValue(string section, string key, string value)
    {
        string fullKey = $"{section}.{key}";
        switch (fullKey.ToLowerInvariant())
        {
            case "general.confirmoverwrite": ConfirmOverwrite = ParseBool(value, ConfirmOverwrite); break;
            case "general.openoutputafterexport": OpenOutputAfterExport = ParseBool(value, OpenOutputAfterExport); break;
            case "general.showoperationwindow": ShowOperationWindow = ParseBool(value, ShowOperationWindow); break;
            case "general.autocloseoperationwindow": AutoCloseOperationWindow = ParseBool(value, AutoCloseOperationWindow); break;
            case "general.relationshipresultlimit": RelationshipResultLimit = ParseInt(value, RelationshipResultLimit, 100, 10000); break;
            case "general.defaultexportdirectory": DefaultExportDirectory = value; break;
            case "general.lastopenedgame": LastOpenedGame = value; break;
            case "display.showlinenumbers": ShowLineNumbers = ParseBool(value, ShowLineNumbers); break;
            case "display.wordwrap": WordWrap = ParseBool(value, WordWrap); break;
            case "display.rememberwindowplacement": RememberWindowPlacement = ParseBool(value, RememberWindowPlacement); break;
            case "export.exportresources": ExportResources = ParseBool(value, ExportResources); break;
            case "export.exportassembly": ExportAssembly = ParseBool(value, ExportAssembly); break;
            case "export.exportindexes": ExportIndexes = ParseBool(value, ExportIndexes); break;
            case "window.width": WindowWidth = ParseDouble(value, WindowWidth); break;
            case "window.height": WindowHeight = ParseDouble(value, WindowHeight); break;
            case "window.left": WindowLeft = ParseDouble(value, WindowLeft); break;
            case "window.top": WindowTop = ParseDouble(value, WindowTop); break;
            case "window.maximized": WindowMaximized = ParseBool(value, WindowMaximized); break;
        }
    }

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out bool parsed) ? parsed : fallback;

    private static int ParseInt(string value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        StringBuilder output = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                output.Append(current);
                continue;
            }

            char escaped = value[++index];
            switch (escaped)
            {
                case '\\':
                    output.Append('\\');
                    break;
                case 'n':
                    output.Append('\n');
                    break;
                case 'r':
                    output.Append('\r');
                    break;
                default:
                    output.Append('\\').Append(escaped);
                    break;
            }
        }
        return output.ToString();
    }

    private static string GetPreferredPath() => Path.Combine(AppContext.BaseDirectory, "SplitGM_Settings.ini");
    private static string GetFallbackPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SplitGM-VM-Decompiler",
        "SplitGM_Settings.ini");
}
