using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SplitGM.Core;

/// <summary>
/// Conservative, report-first repair pass for reconstructed GameMaker projects.
/// Every mutation is applied only when SplitGM can explain the transformation and
/// preserve the pre-repair files. Ambiguous findings are retained as manual-review
/// actions instead of being silently guessed.
/// </summary>
internal static class ReconstructedProjectRepairService
{
    private sealed record GmlFunctionScope(Match Header, int BodyStart, int BodyEnd);

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex FunctionHeaderRegex = new(
        @"\bfunction(?:\s+(?<name>[A-Za-z_][A-Za-z0-9_]*))?\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ArgumentReferenceRegex = new(
        @"\bargument(?<index>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EnumRegex = new(
        @"(?m)^(?<indent>[ \t]*)enum\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VarLineRegex = new(
        @"(?m)^(?<indent>[ \t]*)var\s+(?<declarations>[^;\r\n]+);(?<tail>[ \t]*(?://[^\r\n]*)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CallRegex = new(
        @"(?<![.#])\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> GmlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "while", "repeat", "with", "switch", "case", "default", "return",
        "exit", "break", "continue", "function", "constructor", "new", "delete", "typeof", "instanceof",
        "array", "struct", "static", "enum", "try", "catch", "finally", "throw", "do", "until"
    };

    public static ReconstructionRepairResult Run(
        string outputDirectory,
        string projectFilePath,
        string resourceOrderPath,
        SplitGmProjectDocument document,
        IReadOnlyDictionary<string, string> identifierRenames,
        IReadOnlyCollection<string> knownProjectFunctions,
        IReadOnlyCollection<string> extensionFunctions,
        IEnumerable<ReconstructionRepairAction>? seedActions,
        IProgress<ReconstructionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ArgumentNullException.ThrowIfNull(document);

        string originalOutputDirectory = Path.Combine(outputDirectory, "__SplitGM_OriginalDecompilerOutput");
        string jsonReportPath = Path.Combine(outputDirectory, "SplitGM-Repair-Report.json");
        string textReportPath = Path.Combine(outputDirectory, "SplitGM-Repair-Report.txt");
        string unresolvedPath = Path.Combine(outputDirectory, "SplitGM-Unresolved-Functions.txt");

        ReconstructionRepairReport report = new()
        {
            ProjectFile = Path.GetFileName(projectFilePath),
            TargetProfile = document.Target.ProfileDescription,
            OriginalOutputDirectory = Path.GetRelativePath(outputDirectory, originalOutputDirectory).Replace('\\', '/'),
            ExtensionFunctions = extensionFunctions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList()
        };
        if (seedActions is not null)
            report.Actions.AddRange(seedActions);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.RepairingProject,
            0,
            6,
            "Preserving the original reconstructed output before automatic repair..."));
        PreserveOriginalOutput(outputDirectory, originalOutputDirectory, report, cancellationToken);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.RepairingProject,
            1,
            6,
            "Repairing recovered GML, optional parameters, enums, local declarations, and renamed references..."));
        RepairGmlFiles(outputDirectory, originalOutputDirectory, identifierRenames, report, cancellationToken);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.RepairingProject,
            2,
            6,
            "Normalizing .yy, .yyp, folder, resource-order, and resource-reference data..."));
        RepairJsonFiles(outputDirectory, originalOutputDirectory, projectFilePath, resourceOrderPath, report, cancellationToken);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.RepairingProject,
            3,
            6,
            "Checking object events, room code, instance code, scripts, sprites, and recorded output files..."));
        RepairMissingFilesAndSprites(outputDirectory, originalOutputDirectory, document, report, cancellationToken);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.RepairingProject,
            4,
            6,
            "Generating unresolved-function and extension reports..."));
        AnalyzeFunctions(outputDirectory, originalOutputDirectory, knownProjectFunctions, extensionFunctions, report, cancellationToken);
        WriteUnresolvedFunctions(unresolvedPath, report);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.CompilePreflight,
            5,
            6,
            "Running static compile-preflight validation..."));
        RunCompilePreflight(outputDirectory, originalOutputDirectory, report, cancellationToken);

        WriteJson(jsonReportPath, report);
        WriteTextReport(textReportPath, report);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.CompilePreflight,
            6,
            6,
            report.Preflight.Passed
                ? $"Automatic repair complete: {report.AppliedCount:N0} repair(s); static preflight passed."
                : $"Automatic repair complete: {report.AppliedCount:N0} repair(s); {report.ManualReviewCount:N0} manual-review item(s)."));

        return new ReconstructionRepairResult(
            jsonReportPath,
            textReportPath,
            originalOutputDirectory,
            unresolvedPath,
            report.AppliedCount,
            report.ManualReviewCount,
            report.Preflight);
    }

    private static void PreserveOriginalOutput(
        string outputDirectory,
        string originalOutputDirectory,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(originalOutputDirectory);
        string[] extensions = [".gml", ".yy", ".yyp", ".resource_order", ".splitgmproj"];
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInside(file, originalOutputDirectory) || IsGeneratedRepairReport(file))
                continue;
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(outputDirectory, file);
            string destination = Path.Combine(originalOutputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        AddApplied(report, "PRESERVE-ORIGINAL", "Preservation",
            $"Preserved {copied:N0} pre-repair project/source file(s) before making any automatic changes.",
            Path.GetRelativePath(outputDirectory, originalOutputDirectory).Replace('\\', '/'),
            null, null, RepairConfidence.Certain,
            "The preserved mirror contains the exact files emitted before this repair pass.");
    }

    private static void RepairGmlFiles(
        string outputDirectory,
        string originalOutputDirectory,
        IReadOnlyDictionary<string, string> identifierRenames,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*.gml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInside(path, originalOutputDirectory))
                continue;

            string relative = Relative(outputDirectory, path);
            string original = File.ReadAllText(path);
            string repaired = NormalizeText(original);
            if (!string.Equals(original, repaired, StringComparison.Ordinal))
            {
                AddApplied(report, "GML-TEXT-NORMALIZED", "Recovered GML text normalization",
                    "Removed a UTF-8 BOM and/or normalized recovered GML line endings before applying semantic repairs.",
                    relative, "Original text encoding/line endings", "UTF-8 without BOM and LF line endings",
                    RepairConfidence.Certain,
                    "This changes only the text container representation; token content and execution order are unchanged.");
            }

            repaired = ReplaceRenamedIdentifiers(repaired, identifierRenames, relative, report);
            repaired = RepairDuplicateAndPlaceholderEnums(repaired, relative, report);
            repaired = RepairOptionalFunctionParameters(repaired, relative, report);
            repaired = RepairDuplicateLocalDeclarations(repaired, relative, report);

            if (!string.Equals(original, repaired, StringComparison.Ordinal))
                File.WriteAllText(path, repaired, Utf8NoBom);
        }
    }

    private static string ReplaceRenamedIdentifiers(
        string text,
        IReadOnlyDictionary<string, string> renames,
        string relativePath,
        ReconstructionRepairReport report)
    {
        Dictionary<string, string> usableRenames = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string source, string target) in renames)
        {
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) &&
                IdentifierRegex.IsMatch(source) && IdentifierRegex.IsMatch(target))
            {
                usableRenames[source] = target;
            }
        }
        if (usableRenames.Count == 0)
            return text;

        Dictionary<string, int> replacementCounts = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder output = new(text.Length + 64);
        bool lineComment = false;
        bool blockComment = false;
        bool inString = false;
        bool escape = false;
        char quote = '\0';

        for (int index = 0; index < text.Length;)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (lineComment)
            {
                output.Append(current);
                index++;
                if (current == '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                output.Append(current);
                index++;
                if (current == '*' && next == '/')
                {
                    output.Append('/');
                    index++;
                    blockComment = false;
                }
                continue;
            }
            if (inString)
            {
                output.Append(current);
                index++;
                if (escape)
                    escape = false;
                else if (current == '\\')
                    escape = true;
                else if (current == quote)
                    inString = false;
                continue;
            }
            if (current == '/' && next == '/')
            {
                output.Append("//");
                index += 2;
                lineComment = true;
                continue;
            }
            if (current == '/' && next == '*')
            {
                output.Append("/*");
                index += 2;
                blockComment = true;
                continue;
            }
            if (current is '\'' or '"')
            {
                output.Append(current);
                index++;
                inString = true;
                quote = current;
                escape = false;
                continue;
            }
            if (char.IsLetter(current) || current == '_')
            {
                int tokenStart = index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;
                string token = text[tokenStart..index];
                if (usableRenames.TryGetValue(token, out string? replacement))
                {
                    output.Append(replacement);
                    replacementCounts[token] = replacementCounts.TryGetValue(token, out int count) ? count + 1 : 1;
                }
                else
                {
                    output.Append(token);
                }
                continue;
            }

            output.Append(current);
            index++;
        }

        foreach ((string source, int count) in replacementCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddApplied(report, "GML-RENAMED-REFERENCE", "Broken resource reference",
                $"Updated {count:N0} exact GML identifier reference(s) after a reconstructed resource was renamed.",
                relativePath, source, usableRenames[source], RepairConfidence.High,
                "A token-aware scan changed only complete identifiers outside comments and quoted strings.");
        }
        return output.ToString();
    }

    private static string RepairDuplicateAndPlaceholderEnums(
        string text,
        string relativePath,
        ReconstructionRepairReport report)
    {
        MatchCollection matches = EnumRegex.Matches(text);
        if (matches.Count == 0)
            return text;

        HashSet<string> allocated = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> replacements = [];
        int generated = 1;
        for (int index = 0; index < matches.Count; index++)
        {
            Match match = matches[index];
            string name = match.Groups["name"].Value;
            bool placeholder = name.Equals("__enum__", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("enum_", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith("__enum", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith("placeholder_enum", StringComparison.OrdinalIgnoreCase);
            if (!placeholder && allocated.Add(name))
                continue;

            string replacement;
            do
            {
                replacement = $"SGM_RecoveredEnum_{generated++:D3}";
            }
            while (!allocated.Add(replacement));
            replacements[index] = replacement;
        }
        if (replacements.Count == 0)
            return text;

        StringBuilder output = new(text.Length + replacements.Count * 16);
        int cursor = 0;
        for (int index = 0; index < matches.Count; index++)
        {
            Match match = matches[index];
            int nextStart = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            if (!replacements.TryGetValue(index, out string? replacement))
                continue;

            string name = match.Groups["name"].Value;
            output.Append(text, cursor, match.Groups["name"].Index - cursor);
            output.Append(replacement);
            int segmentStart = match.Groups["name"].Index + match.Groups["name"].Length;
            string segment = text[segmentStart..nextStart];
            Regex qualifiedReference = new(
                $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?=\s*\.)",
                RegexOptions.CultureInvariant);
            int referenceCount = qualifiedReference.Matches(segment).Count;
            output.Append(qualifiedReference.Replace(segment, replacement));
            cursor = nextStart;

            AddApplied(report, "GML-ENUM-NAME", "Duplicate placeholder enum name",
                name.StartsWith("__enum", StringComparison.OrdinalIgnoreCase) || name.StartsWith("placeholder_enum", StringComparison.OrdinalIgnoreCase)
                    ? "Replaced a decompiler placeholder enum name with a unique stable identifier."
                    : "Renamed a duplicate enum declaration to avoid a case-insensitive compile collision.",
                relativePath, name, replacement, RepairConfidence.Medium,
                $"The declaration and {referenceCount:N0} qualified reference(s) before the next enum declaration were updated together; review cross-file enum references manually.");
        }
        output.Append(text, cursor, text.Length - cursor);
        return output.ToString();
    }

    private static string RepairOptionalFunctionParameters(
        string text,
        string relativePath,
        ReconstructionRepairReport report)
    {
        MatchCollection matches = FunctionHeaderRegex.Matches(text);
        if (matches.Count == 0)
            return text;

        IReadOnlyList<GmlFunctionScope> scopes = FindFunctionScopes(text, matches);
        Dictionary<int, GmlFunctionScope> scopesByHeader = scopes.ToDictionary(
            scope => scope.Header.Index,
            scope => scope);
        StringBuilder result = new(text);
        for (int matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
        {
            Match match = matches[matchIndex];
            if (!scopesByHeader.TryGetValue(match.Index, out GmlFunctionScope? scope))
            {
                AddManual(report, "GML-FUNCTION-BODY-NOT-FOUND", "Recovered optional parameters",
                    $"Could not locate a balanced body for recovered function header at character {match.Index:N0}.",
                    relativePath, RepairConfidence.ManualReview,
                    "Inspect the preserved GML and VM assembly, repair the function braces, and then review its argument slots.");
                continue;
            }

            int bodyBase = scope.BodyStart + 1;
            string body = text[bodyBase..scope.BodyEnd];
            char[] argumentScan = StripCommentsAndStrings(body).ToCharArray();
            foreach (GmlFunctionScope nestedScope in scopes)
            {
                if (nestedScope.Header.Index <= scope.Header.Index ||
                    nestedScope.BodyEnd >= scope.BodyEnd ||
                    nestedScope.Header.Index < bodyBase)
                    continue;

                int nestedStart = Math.Clamp(nestedScope.Header.Index - bodyBase, 0, argumentScan.Length);
                int nestedEnd = Math.Clamp(nestedScope.BodyEnd + 1 - bodyBase, nestedStart, argumentScan.Length);
                Array.Fill(argumentScan, ' ', nestedStart, nestedEnd - nestedStart);
            }

            int highestArgument = -1;
            foreach (Match argumentMatch in ArgumentReferenceRegex.Matches(new string(argumentScan)))
            {
                if (int.TryParse(argumentMatch.Groups["index"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out int index))
                    highestArgument = Math.Max(highestArgument, index);
            }
            if (highestArgument < 0)
                continue;

            string parameterText = match.Groups["parameters"].Value.Trim();
            List<string> parameters = SplitCommaAware(parameterText);
            int declared = parameters.Count(item => !string.IsNullOrWhiteSpace(item));
            if (highestArgument < declared)
                continue;
            if (highestArgument > 63)
            {
                AddManual(report, "GML-OPTIONAL-PARAMETER-LIMIT", "Recovered optional parameters",
                    $"A recovered function references argument{highestArgument}, which exceeds SplitGM's safe automatic parameter limit.",
                    relativePath, RepairConfidence.ManualReview,
                    "Inspect the original VM assembly and add only the parameters actually required by the function.",
                    "Prefer default values of undefined for optional recovered arguments.");
                continue;
            }

            List<string> additions = [];
            for (int index = declared; index <= highestArgument; index++)
                additions.Add($"_sgm_arg{index} = undefined");
            string replacementParameters = string.Join(", ", parameters.Where(item => !string.IsNullOrWhiteSpace(item)).Concat(additions));
            int groupStart = match.Groups["parameters"].Index;
            result.Remove(groupStart, match.Groups["parameters"].Length);
            result.Insert(groupStart, replacementParameters);

            string functionName = match.Groups["name"].Success ? match.Groups["name"].Value : "<anonymous>";
            AddApplied(report, "GML-OPTIONAL-PARAMETERS", "Recovered optional parameters",
                $"Added {additions.Count:N0} optional parameter(s) to recovered function {functionName} because its balanced body references higher argument slots.",
                relativePath, parameterText, replacementParameters, RepairConfidence.Medium,
                "The added parameters default to undefined, preserving calls that provide fewer arguments; comments, strings, and nested function bodies are not mistaken for this function's argument usage.");
        }
        return result.ToString();
    }

    private static string RepairDuplicateLocalDeclarations(
        string text,
        string relativePath,
        ReconstructionRepairReport report)
    {
        MatchCollection functionHeaders = FunctionHeaderRegex.Matches(text);
        IReadOnlyList<GmlFunctionScope> scopes = FindFunctionScopes(text, functionHeaders);
        Dictionary<int, HashSet<string>> declaredByScope = [];

        return VarLineRegex.Replace(text, match =>
        {
            int scopeKey = FindInnermostFunctionScopeKey(scopes, match.Index);
            if (!declaredByScope.TryGetValue(scopeKey, out HashSet<string>? declaredNames))
            {
                declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                declaredByScope[scopeKey] = declaredNames;
            }

            string declarationsText = match.Groups["declarations"].Value;
            List<string> declarations = SplitCommaAware(declarationsText);
            List<string> kept = [];
            List<string> followUpAssignments = [];
            bool changed = false;

            foreach (string declaration in declarations)
            {
                string trimmed = declaration.Trim();
                string identifier = ExtractDeclarationIdentifier(trimmed);
                if (identifier.Length == 0)
                {
                    kept.Add(trimmed);
                    continue;
                }

                int equalsIndex = trimmed.IndexOf('=');
                if (!declaredNames.Add(identifier))
                {
                    changed = true;
                    if (equalsIndex >= 0)
                    {
                        string initializer = trimmed[(equalsIndex + 1)..].Trim();
                        if (initializer.Length > 0)
                        {
                            followUpAssignments.Add(identifier + " = " + initializer);
                            AddApplied(report, "GML-DUPLICATE-LOCAL-INITIALIZER", "Duplicate local-variable declaration",
                                $"Converted a duplicate declaration of local {identifier} into a normal assignment in the same recovered function scope.",
                                relativePath, trimmed, identifier + " = " + initializer, RepairConfidence.High,
                                "GameMaker locals are function-scoped; preserving the duplicate initializer as an assignment keeps its execution order without redeclaring the local.");
                            continue;
                        }
                    }

                    AddApplied(report, "GML-DUPLICATE-LOCAL", "Duplicate local-variable declaration",
                        $"Removed a duplicate bare declaration of local {identifier} from the same recovered function scope.",
                        relativePath, trimmed, null, RepairConfidence.High,
                        "The local was already declared earlier in the same balanced function body and the duplicate carried no usable initializer.");
                    continue;
                }

                kept.Add(trimmed);
            }

            if (!changed)
                return match.Value;

            string indent = match.Groups["indent"].Value;
            string tail = match.Groups["tail"].Value;
            StringBuilder replacement = new();
            if (kept.Count == 0)
                replacement.Append(indent).Append("// SplitGM repair: duplicate bare local declaration removed.");
            else
                replacement.Append(indent).Append("var ").Append(string.Join(", ", kept)).Append(';');

            foreach (string assignment in followUpAssignments)
                replacement.Append('\n').Append(indent).Append(assignment).Append(';');
            replacement.Append(tail);
            return replacement.ToString();
        });
    }

    private static void RepairJsonFiles(
        string outputDirectory,
        string originalOutputDirectory,
        string projectFilePath,
        string resourceOrderPath,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        string[] patterns = ["*.yy", "*.yyp", "*.resource_order"];
        foreach (string pattern in patterns)
        {
            foreach (string path in Directory.EnumerateFiles(outputDirectory, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsInside(path, originalOutputDirectory))
                    continue;

                string relative = Relative(outputDirectory, path);
                string original = File.ReadAllText(path);
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(original);
                }
                catch (JsonException exception)
                {
                    AddManual(report, "JSON-PARSE-FAILED", "Malformed project JSON",
                        $"Could not parse {relative}: {exception.Message}", relative, RepairConfidence.ManualReview,
                        "Compare this file with the preserved copy and recreate the malformed object/array delimiter.",
                        "Open the file in a JSON-aware editor before loading the project in GameMaker.");
                    continue;
                }
                if (node is null)
                    continue;

                bool changed = false;
                if (node is JsonObject root)
                {
                    changed |= RepairTopLevelDefaults(path, root, report, relative);
                    changed |= RepairResourceReferences(outputDirectory, root, report, relative);
                    if (Path.GetExtension(path).Equals(".yyp", StringComparison.OrdinalIgnoreCase))
                        changed |= RepairProjectCollections(outputDirectory, root, report, relative);
                    if (Path.GetExtension(path).Equals(".resource_order", StringComparison.OrdinalIgnoreCase))
                        changed |= RepairResourceOrder(root, report, relative);
                }

                string normalized = node.ToJsonString(JsonOptions);
                if (changed || !JsonEquivalentFormatting(original, normalized))
                    File.WriteAllText(path, normalized, Utf8NoBom);
            }
        }

        if (!File.Exists(resourceOrderPath))
        {
            JsonObject order = new()
            {
                ["FolderOrderSettings"] = new JsonArray(),
                ["ResourceOrderSettings"] = new JsonArray()
            };
            WriteJson(resourceOrderPath, order);
            AddApplied(report, "RESOURCE-ORDER-CREATED", "Malformed resource-order data",
                "Created a missing .resource_order file with valid empty collections.",
                Relative(outputDirectory, resourceOrderPath), null, "Valid resource-order JSON", RepairConfidence.High,
                "GameMaker can repopulate ordering after opening the project.");
        }

        if (!File.Exists(projectFilePath))
        {
            AddManual(report, "YYP-MISSING", "Malformed .yyp data",
                "The reconstructed .yyp project file is missing and cannot be regenerated safely without its resource catalog.",
                Relative(outputDirectory, projectFilePath), RepairConfidence.ManualReview,
                "Run reconstructed-project export again and retain the complete output directory.");
        }
    }

    private static bool RepairTopLevelDefaults(
        string path,
        JsonObject root,
        ReconstructionRepairReport report,
        string relative)
    {
        bool changed = false;
        string extension = Path.GetExtension(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string? resourceType = GetString(root["resourceType"]);
        string inferredType = InferResourceType(path, resourceType);

        if (string.IsNullOrWhiteSpace(resourceType) && inferredType.Length > 0)
        {
            root["resourceType"] = inferredType;
            changed = true;
            AddApplied(report, "JSON-RESOURCE-TYPE", "Missing target-version default fields",
                $"Added missing resourceType {inferredType}.", relative, null, inferredType,
                RepairConfidence.High, "The resource folder/file type uniquely identifies the expected GameMaker resource type.");
        }
        if (root["resourceVersion"] is null && (extension.Equals(".yy", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yyp", StringComparison.OrdinalIgnoreCase)))
        {
            root["resourceVersion"] = "2.0";
            changed = true;
            AddApplied(report, "JSON-RESOURCE-VERSION", "Missing target-version default fields",
                "Added missing resourceVersion 2.0.", relative, null, "2.0", RepairConfidence.High,
                "The selected reconstruction target uses modern GameMaker 2.x JSON resources.");
        }

        if (extension.Equals(".yy", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yyp", StringComparison.OrdinalIgnoreCase))
        {
            string canonicalName = GetString(root["name"]) ?? GetString(root["%Name"]) ?? stem;
            if (!IdentifierRegex.IsMatch(canonicalName) && IdentifierRegex.IsMatch(stem))
                canonicalName = stem;
            if (GetString(root["name"]) != canonicalName)
            {
                string? before = root["name"]?.ToJsonString();
                root["name"] = canonicalName;
                changed = true;
                AddApplied(report, "JSON-NAME", "Invalid or inconsistent resource name",
                    "Synchronized the top-level name with the safe reconstructed resource identifier.",
                    relative, before, canonicalName, RepairConfidence.High,
                    "The resource filename and safe identifier provide an unambiguous canonical name.");
            }
            if (GetString(root["%Name"]) != canonicalName)
            {
                string? before = root["%Name"]?.ToJsonString();
                root["%Name"] = canonicalName;
                changed = true;
                AddApplied(report, "JSON-PERCENT-NAME", "Invalid or inconsistent resource name",
                    "Synchronized %Name with the resource name.", relative, before, canonicalName,
                    RepairConfidence.High, "Modern GameMaker requires consistent %Name/name values in generated resources.");
            }
        }

        string tag = TagForResourceType(inferredType);
        if (tag.Length > 0 && root[tag] is null)
        {
            root[tag] = tag is "$GMObject" or "$GMFolder" or "$GMPath" ? "" : "v1";
            changed = true;
            AddApplied(report, "JSON-TYPE-TAG", "Missing target-version default fields",
                $"Added missing modern GameMaker type tag {tag}.", relative, null, tag,
                RepairConfidence.Medium, "The resourceType maps directly to a known modern GameMaker type tag.");
        }

        return changed;
    }

    private static bool RepairResourceReferences(
        string outputDirectory,
        JsonObject root,
        ReconstructionRepairReport report,
        string relative)
    {
        bool changed = false;
        foreach (JsonObject resourceId in EnumerateObjects(root))
        {
            if (resourceId["path"] is not JsonValue pathValue || !pathValue.TryGetValue(out string? referencePath) || string.IsNullOrWhiteSpace(referencePath))
                continue;
            string normalized = referencePath.Replace('\\', '/').TrimStart('/');
            if (IsVirtualProjectReference(normalized))
                continue;
            string? corrected = ResolveCaseInsensitiveRelativePath(outputDirectory, normalized);
            if (corrected is null)
            {
                AddManual(report, "JSON-BROKEN-REFERENCE", "Broken resource reference after renaming",
                    $"Resource reference {normalized} does not resolve to an exported file.", relative,
                    RepairConfidence.ManualReview,
                    "Locate the intended resource in the preserved output and update both name and path together.",
                    "Check whether the referenced resource was fallback-only and therefore cannot be represented in the .yyp.");
                continue;
            }
            if (!string.Equals(referencePath, corrected, StringComparison.Ordinal))
            {
                resourceId["path"] = corrected;
                changed = true;
                AddApplied(report, "JSON-REFERENCE-PATH", "Broken resource reference after renaming",
                    "Corrected a resource path's slash/case spelling to match the exported file.",
                    relative, referencePath, corrected, RepairConfidence.High,
                    "The corrected path resolved uniquely by case-insensitive component matching inside the project directory.");
            }

            string targetStem = Path.GetFileNameWithoutExtension(corrected);
            if (resourceId["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? oldName) &&
                !string.IsNullOrWhiteSpace(targetStem) &&
                !string.Equals(oldName, targetStem, StringComparison.Ordinal) &&
                ShouldSynchronizeReferenceName(root, resourceId, corrected, oldName))
            {
                resourceId["name"] = targetStem;
                changed = true;
                AddApplied(report, "JSON-REFERENCE-NAME", "Broken resource reference after renaming",
                    "Synchronized a resource reference name with its resolved .yy file.",
                    relative, oldName, targetStem, RepairConfidence.High,
                    "The path resolved uniquely to a concrete resource file.");
            }
        }
        return changed;
    }

    private static bool ShouldSynchronizeReferenceName(
        JsonObject root,
        JsonObject resourceId,
        string correctedPath,
        string oldName)
    {
        string rootType = GetString(root["resourceType"]) ?? string.Empty;
        if (!rootType.Equals("GMRoom", StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedPath = correctedPath.Replace('\\', '/');
        if (!normalizedPath.StartsWith("rooms/", StringComparison.OrdinalIgnoreCase))
            return true;

        string roomName = GetString(root["name"]) ?? Path.GetFileNameWithoutExtension(correctedPath);
        bool pointsToThisRoom = normalizedPath.EndsWith(
            "/" + roomName + ".yy",
            StringComparison.OrdinalIgnoreCase);
        bool looksLikeInstanceName = oldName.StartsWith("inst_", StringComparison.OrdinalIgnoreCase);
        bool isFullResourceObject = resourceId["resourceType"] is not null || resourceId["$GMRInstance"] is not null;

        // GMRoom.instanceCreationOrder entries intentionally use the instance
        // name with the containing room .yy path. Synchronizing the name to the
        // room file stem makes GameMaker link a room where it expects an instance.
        return !(pointsToThisRoom && looksLikeInstanceName && !isFullResourceObject);
    }

    private static bool RepairProjectCollections(
        string outputDirectory,
        JsonObject root,
        ReconstructionRepairReport report,
        string relative)
    {
        bool changed = false;
        foreach (string property in new[] { "resources", "Folders", "RoomOrderNodes", "AudioGroups", "TextureGroups", "IncludedFiles" })
        {
            if (root[property] is JsonArray)
                continue;

            string? before = root[property]?.ToJsonString();
            root[property] = new JsonArray();
            changed = true;
            AddApplied(report, "YYP-MISSING-COLLECTION", "Missing target-version default fields",
                $"Replaced missing or malformed .yyp collection {property} with an empty array.", relative, before, "[]",
                RepairConfidence.High, "GameMaker expects this collection to be an array even when it is empty.");
        }
        if (root["configs"] is not JsonObject configs)
        {
            string? before = root["configs"]?.ToJsonString();
            configs = new JsonObject { ["children"] = new JsonArray(), ["name"] = "Default" };
            root["configs"] = configs;
            changed = true;
            AddApplied(report, "YYP-CONFIGS", "Missing target-version default fields",
                "Replaced a missing or malformed project configuration object with the default configuration.", relative, before, "Default config",
                RepairConfidence.High, "A default configuration object is required by the selected modern GameMaker target profile.");
        }
        else
        {
            if (configs["children"] is not JsonArray)
            {
                configs["children"] = new JsonArray();
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(GetString(configs["name"])))
            {
                configs["name"] = "Default";
                changed = true;
            }
        }

        if (root["resources"] is JsonArray resources)
        {
            HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                if (resources[index] is not JsonObject entry || entry["id"] is not JsonObject id)
                    continue;
                string path = GetString(id["path"]) ?? string.Empty;
                string name = GetString(id["name"]) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    AddManual(report, "YYP-EMPTY-RESOURCE-PATH", "Malformed .yyp resource data",
                        "A .yyp resource entry has no usable path and was left in place for manual review.", relative,
                        RepairConfidence.ManualReview,
                        "Recover the intended resource path from __SplitGM_OriginalDecompilerOutput, or remove the entry only after confirming it is not referenced.");
                }
                else if (!seenPaths.Add(path))
                {
                    resources.RemoveAt(index);
                    changed = true;
                    AddApplied(report, "YYP-DUPLICATE-PATH", "Malformed .yyp resource data",
                        "Removed a duplicate .yyp resource entry with the same path.", relative, path, null,
                        RepairConfidence.Certain, "Both entries referred to the same case-insensitive project path.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    AddManual(report, "YYP-EMPTY-RESOURCE-NAME", "Malformed .yyp resource data",
                        "A .yyp resource entry has no usable name and was left in place for manual review.", relative,
                        RepairConfidence.ManualReview,
                        "Synchronize the entry name with the referenced .yy file's safe name and %Name values.");
                }
                else if (!seenNames.Add(name))
                {
                    AddManual(report, "YYP-DUPLICATE-NAME", "Case-insensitive name collision",
                        $"Multiple .yyp resources use the case-insensitive name {name}.", relative,
                        RepairConfidence.ManualReview,
                        "Rename one resource folder, .yy file, name/%Name fields, and every ResourceId reference as one atomic change.");
                }
                if (path.Length > 0 && ResolveCaseInsensitiveRelativePath(outputDirectory, path) is null)
                {
                    AddManual(report, "YYP-MISSING-RESOURCE", "Missing resource file",
                        $"The .yyp lists {path}, but that resource does not exist.", relative,
                        RepairConfidence.ManualReview,
                        "Recover the resource from __SplitGM_OriginalDecompilerOutput or remove the entry only if it is truly unrepresentable.");
                }
            }
        }
        return changed;
    }

    private static bool RepairResourceOrder(JsonObject root, ReconstructionRepairReport report, string relative)
    {
        bool changed = false;
        foreach (string property in new[] { "FolderOrderSettings", "ResourceOrderSettings" })
        {
            if (root[property] is not JsonArray array)
            {
                root[property] = new JsonArray();
                changed = true;
                AddApplied(report, "ORDER-MISSING-COLLECTION", "Malformed resource-order data",
                    $"Replaced missing or malformed {property} with a valid array.", relative, null, "[]",
                    RepairConfidence.High, "Ordering is non-semantic and GameMaker can regenerate it.");
                continue;
            }

            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            int order = 1;
            for (int index = array.Count - 1; index >= 0; index--)
            {
                if (array[index] is not JsonObject item)
                {
                    array.RemoveAt(index);
                    changed = true;
                    continue;
                }
                string path = GetString(item["path"]) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    AddManual(report, "ORDER-EMPTY-PATH", "Malformed resource-order data",
                        $"An entry in {property} has no usable path and was left in place for manual review.", relative,
                        RepairConfidence.ManualReview,
                        "Restore the intended folder/resource path from the preserved project output, or remove the ordering entry if it is confirmed to be orphaned.");
                }
                else if (!paths.Add(path))
                {
                    array.RemoveAt(index);
                    changed = true;
                    AddApplied(report, "ORDER-DUPLICATE", "Malformed resource-order data",
                        "Removed a duplicate ordering entry.", relative, path, null,
                        RepairConfidence.Certain, "Ordering entries with identical paths are redundant.");
                }
            }
            foreach (JsonObject item in array.OfType<JsonObject>())
            {
                if (GetInt(item["order"], int.MinValue) != order)
                {
                    item["order"] = order;
                    changed = true;
                }
                order++;
            }
        }
        return changed;
    }

    private static void RepairMissingFilesAndSprites(
        string outputDirectory,
        string originalOutputDirectory,
        SplitGmProjectDocument document,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        foreach (SplitGmProjectResource resource in document.Resources.Where(item => item.ExportSucceeded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string relative in resource.Files)
            {
                string full = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    continue;

                if (Path.GetExtension(full).Equals(".gml", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full,
                        "/// @description SplitGM automatic repair placeholder\n" +
                        "// The compiled resource referenced code that was not recoverable from the data archive.\n",
                        Utf8NoBom);
                    AddApplied(report, "MISSING-GML-CREATED", "Missing object-event, room-code, or instance-code file",
                        "Created a safe placeholder for a recorded but missing GML file.", relative, null,
                        "Placeholder GML", RepairConfidence.Medium,
                        "The placeholder keeps the project structurally loadable but cannot reconstruct lost behavior.");
                }
                else
                {
                    AddManual(report, "RECORDED-FILE-MISSING", "Missing reconstructed output file",
                        $"The intermediate project records {relative}, but the file is missing.", relative,
                        RepairConfidence.ManualReview,
                        "Recover the file from the preserved output, or export the source resource again.");
                }
            }
        }

        foreach (string yyPath in Directory.EnumerateFiles(outputDirectory, "*.yy", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInside(yyPath, originalOutputDirectory))
                continue;
            JsonObject? root = TryReadObject(yyPath);
            if (root is null)
                continue;
            string type = GetString(root["resourceType"]) ?? string.Empty;
            string relativeYy = Relative(outputDirectory, yyPath);

            if (type.Equals("GMScript", StringComparison.OrdinalIgnoreCase))
            {
                string gml = Path.ChangeExtension(yyPath, ".gml");
                EnsurePlaceholderGml(outputDirectory, gml, "script source", report);
            }
            else if (type.Equals("GMObject", StringComparison.OrdinalIgnoreCase))
            {
                RepairObjectEventFiles(outputDirectory, yyPath, root, report);
            }
            else if (type.Equals("GMRoom", StringComparison.OrdinalIgnoreCase))
            {
                RepairRoomCodeFiles(outputDirectory, yyPath, root, report);
            }
            else if (type.Equals("GMSprite", StringComparison.OrdinalIgnoreCase))
            {
                if (RepairSpriteJson(yyPath, root, relativeYy, report))
                    WriteJson(yyPath, root);
            }
        }
    }

    private static void RepairObjectEventFiles(
        string outputDirectory,
        string yyPath,
        JsonObject root,
        ReconstructionRepairReport report)
    {
        if (root["eventList"] is not JsonArray events)
            return;
        string directory = Path.GetDirectoryName(yyPath)!;
        foreach (JsonObject eventObject in events.OfType<JsonObject>())
        {
            int eventType = GetInt(eventObject["eventType"], 0);
            int eventNum = GetInt(eventObject["eventNum"], 0);
            string stem;
            if (eventType == 4 && eventObject["collisionObjectId"] is JsonObject collision)
            {
                string collisionName = GetString(collision["name"]) ?? "UnknownObject";
                stem = "Collision_" + SafeIdentifier(collisionName, "UnknownObject");
            }
            else
            {
                stem = EventStem(eventType) + "_" + eventNum.ToString(CultureInfo.InvariantCulture);
            }
            EnsurePlaceholderGml(outputDirectory, Path.Combine(directory, stem + ".gml"), "object event source", report);
        }
    }

    private static void RepairRoomCodeFiles(
        string outputDirectory,
        string yyPath,
        JsonObject root,
        ReconstructionRepairReport report)
    {
        string directory = Path.GetDirectoryName(yyPath)!;
        string creationCode = GetString(root["creationCodeFile"]) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(creationCode))
            EnsurePlaceholderGml(outputDirectory, Path.Combine(directory, creationCode), "room creation code", report);

        if (root["layers"] is not JsonArray layers)
            return;
        foreach (JsonObject instance in EnumerateObjects(layers))
        {
            if (GetString(instance["resourceType"])?.Equals("GMRInstance", StringComparison.OrdinalIgnoreCase) != true ||
                !GetBool(instance["hasCreationCode"], false))
                continue;
            string name = GetString(instance["name"]) ?? GetString(instance["%Name"]) ?? "instance";
            EnsurePlaceholderGml(outputDirectory,
                Path.Combine(directory, "InstanceCreationCode_" + SafeIdentifier(name, "instance") + ".gml"),
                "room instance creation code", report);
        }
    }

    private static bool RepairSpriteJson(
        string yyPath,
        JsonObject root,
        string relative,
        ReconstructionRepairReport report)
    {
        bool changed = false;
        int sourceWidth = GetInt(root["width"], 1);
        int sourceHeight = GetInt(root["height"], 1);
        int width = Math.Clamp(sourceWidth, 1, 262_144);
        int height = Math.Clamp(sourceHeight, 1, 262_144);
        bool canvasChanged = SetInt(root, "width", width) | SetInt(root, "height", height);
        if (canvasChanged)
        {
            changed = true;
            AddApplied(report, "SPRITE-CANVAS", "Sprite canvas/padding inconsistency",
                "Normalized missing, nonnumeric, zero, negative, or unsafe sprite canvas dimensions.",
                relative, $"{sourceWidth}x{sourceHeight}", $"{width}x{height}", RepairConfidence.High,
                "The repaired dimensions are positive and bounded to prevent integer overflow and unusable project metadata.");
        }

        int left = Math.Clamp(GetInt(root["bbox_left"], 0), 0, width - 1);
        int right = Math.Clamp(GetInt(root["bbox_right"], width - 1), left, width - 1);
        int top = Math.Clamp(GetInt(root["bbox_top"], 0), 0, height - 1);
        int bottom = Math.Clamp(GetInt(root["bbox_bottom"], height - 1), top, height - 1);
        bool bboxChanged = SetInt(root, "bbox_left", left) | SetInt(root, "bbox_right", right) |
                           SetInt(root, "bbox_top", top) | SetInt(root, "bbox_bottom", bottom);
        if (bboxChanged)
        {
            changed = true;
            AddApplied(report, "SPRITE-BBOX", "Sprite collision-mask inconsistency",
                "Clamped and ordered the sprite collision bounding box inside its canvas.",
                relative, null, $"{left},{top}..{right},{bottom}", RepairConfidence.High,
                "A collision box outside the sprite canvas is invalid in the selected target profile.");
        }

        if (root["sequence"] is not JsonObject sequence)
        {
            sequence = new JsonObject();
            root["sequence"] = sequence;
            changed = true;
        }
        int xorigin = Math.Clamp(GetInt(sequence["xorigin"], 0), -width * 8, width * 8);
        int yorigin = Math.Clamp(GetInt(sequence["yorigin"], 0), -height * 8, height * 8);
        bool originChanged = SetInt(sequence, "xorigin", xorigin) | SetInt(sequence, "yorigin", yorigin);
        if (originChanged)
        {
            changed = true;
            AddApplied(report, "SPRITE-ORIGIN", "Sprite origin inconsistency",
                "Normalized missing or extreme sprite origin values.", relative, null,
                $"{xorigin},{yorigin}", RepairConfidence.Medium,
                "The origin remains permitted outside the canvas but is bounded to a repair-safe range.");
        }

        if (root["frames"] is not JsonArray frames)
        {
            root["frames"] = new JsonArray();
            frames = (JsonArray)root["frames"]!;
            changed = true;
            AddApplied(report, "SPRITE-FRAMES", "Sprite canvas/padding inconsistency",
                "Created a missing frames array.", relative, null, "[]", RepairConfidence.Medium,
                "No pixel data was invented; missing frames remain visible in the report.");
        }

        string directory = Path.GetDirectoryName(yyPath)!;
        foreach (JsonObject frame in frames.OfType<JsonObject>())
        {
            string id = GetString(frame["name"]) ?? GetString(frame["%Name"]) ?? string.Empty;
            if (id.Length == 0)
                continue;
            string png = Path.Combine(directory, id + ".png");
            if (!File.Exists(png))
            {
                AddManual(report, "SPRITE-FRAME-PNG-MISSING", "Sprite canvas/padding inconsistency",
                    $"Sprite frame {id}.png is missing.", relative, RepairConfidence.ManualReview,
                    "Recover the frame PNG from __SplitGM_OriginalDecompilerOutput or re-export the source texture page item.",
                    "Do not replace it with an arbitrary blank image unless losing the frame is acceptable.");
            }
        }

        foreach ((string key, JsonNode? defaultValue) in new (string, JsonNode?)[]
        {
            ("bboxMode", 0), ("collisionKind", 1), ("collisionTolerance", 0), ("edgeFiltering", false),
            ("preMultiplyAlpha", false), ("HTile", false), ("VTile", false), ("type", 0)
        })
        {
            if (root[key] is null)
            {
                root[key] = defaultValue?.DeepClone();
                changed = true;
            }
        }

        int frameCount = frames.Count;
        if (Math.Abs(GetDouble(sequence["length"], -1) - Math.Max(1, frameCount)) > double.Epsilon)
        {
            sequence["length"] = Math.Max(1, frameCount);
            changed = true;
        }
        bool playbackDefaultsChanged = false;
        playbackDefaultsChanged |= SetDefault(sequence, "playback", 1);
        playbackDefaultsChanged |= SetDefault(sequence, "playbackSpeed", 30.0);
        playbackDefaultsChanged |= SetDefault(sequence, "playbackSpeedType", 0);
        playbackDefaultsChanged |= SetDefault(sequence, "timeUnits", 1);
        if (playbackDefaultsChanged)
        {
            changed = true;
            AddApplied(report, "SPRITE-PLAYBACK-DEFAULTS", "Missing target-version default fields",
                "Added missing sprite sequence playback defaults.", relative, null,
                "playback=1; playbackSpeed=30; playbackSpeedType=0; timeUnits=1",
                RepairConfidence.High, "These are the conservative defaults used by SplitGM's selected modern GameMaker target profile.");
        }
        return changed;
    }

    private static void AnalyzeFunctions(
        string outputDirectory,
        string originalOutputDirectory,
        IReadOnlyCollection<string> knownProjectFunctions,
        IReadOnlyCollection<string> extensionFunctions,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        HashSet<string> known = new(knownProjectFunctions, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(extensionFunctions);
        HashSet<string> definitions = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> calls = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*.gml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInside(path, originalOutputDirectory))
                continue;
            string text = StripCommentsAndStrings(File.ReadAllText(path));
            foreach (Match match in FunctionHeaderRegex.Matches(text))
            {
                if (match.Groups["name"].Success)
                    definitions.Add(match.Groups["name"].Value);
            }
            foreach (Match match in CallRegex.Matches(text))
            {
                string name = match.Groups["name"].Value;
                if (!GmlKeywords.Contains(name))
                    calls.Add(name);
            }
        }
        known.UnionWith(definitions);

        foreach (string call in calls.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(call) || IsLikelyBuiltin(call))
                continue;
            if (!IsProjectSpecificFunctionCandidate(call))
                continue;
            report.UnresolvedFunctions.Add(call);
            AddManual(report, "UNRESOLVED-FUNCTION", "Unresolved function report",
                $"Could not resolve project/extension function candidate {call}.", null,
                RepairConfidence.Low,
                "Search VM assembly and the original extension table for the function's true declaration.",
                "Check whether the function was renamed, stripped, YYC-only, or supplied by a missing extension.");
        }
    }

    private static void RunCompilePreflight(
        string outputDirectory,
        string originalOutputDirectory,
        ReconstructionRepairReport report,
        CancellationToken cancellationToken)
    {
        ReconstructionCompilePreflight preflight = report.Preflight;
        Dictionary<string, List<string>> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInside(path, originalOutputDirectory))
                continue;
            string extension = Path.GetExtension(path);
            bool isYy = extension.Equals(".yy", StringComparison.OrdinalIgnoreCase);
            bool isYyp = extension.Equals(".yyp", StringComparison.OrdinalIgnoreCase);
            bool isResourceOrder = extension.Equals(".resource_order", StringComparison.OrdinalIgnoreCase);
            if (isYy || isYyp || isResourceOrder)
            {
                preflight.JsonFilesChecked++;
                JsonObject? root = TryReadObject(path);
                if (root is null)
                {
                    preflight.JsonParseErrors++;
                    continue;
                }
                if (isYy || isYyp)
                {
                    string? name = GetString(root["name"]);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        if (!IdentifierRegex.IsMatch(name))
                            preflight.InvalidIdentifiers++;
                        string type = GetString(root["resourceType"]) ?? extension;
                        names.GetOrAdd(type + ":" + name).Add(Relative(outputDirectory, path));
                    }
                }
                foreach (JsonObject id in EnumerateObjects(root))
                {
                    string? referenced = GetString(id["path"]);
                    if (!string.IsNullOrWhiteSpace(referenced) &&
                        !IsVirtualProjectReference(referenced) &&
                        ResolveCaseInsensitiveRelativePath(outputDirectory, referenced) is null)
                        preflight.BrokenResourceReferences++;
                }
            }
            else if (extension.Equals(".gml", StringComparison.OrdinalIgnoreCase))
            {
                preflight.GmlFilesChecked++;
                string source = File.ReadAllText(path);
                if (!HasBalancedGml(source))
                    preflight.GmlBalanceErrors++;
            }
        }

        preflight.DuplicateNames = names.Values.Count(paths => paths.Count > 1);
        preflight.UnresolvedFunctionCandidates = report.UnresolvedFunctions.Count;
        preflight.MissingFiles += report.Actions.Count(action =>
            !action.Applied && action.Id is "RECORDED-FILE-MISSING" or "YYP-MISSING-RESOURCE" or "SPRITE-FRAME-PNG-MISSING");

        AddApplied(report, "COMPILE-PREFLIGHT", "Project compile-preflight validation",
            preflight.Passed
                ? "Static compile-preflight completed without structural blockers."
                : "Static compile-preflight completed and recorded remaining structural blockers.",
            null, null,
            $"JSON errors={preflight.JsonParseErrors}; GML balance errors={preflight.GmlBalanceErrors}; missing={preflight.MissingFiles}; broken refs={preflight.BrokenResourceReferences}",
            preflight.Passed ? RepairConfidence.High : RepairConfidence.ManualReview,
            "This is a static preflight, not a GameMaker IDE compile; target-runner semantic errors may still remain.");
    }

    private static void WriteUnresolvedFunctions(string path, ReconstructionRepairReport report)
    {
        StringBuilder text = new();
        text.AppendLine("SplitGM unresolved function and extension report");
        text.AppendLine("===============================================");
        text.AppendLine($"Generated by: {SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}");
        text.AppendLine();
        text.AppendLine("Recovered extension functions");
        text.AppendLine("-----------------------------");
        if (report.ExtensionFunctions.Count == 0)
            text.AppendLine("(No extension function names were recoverable from the compiled data.)");
        else
            foreach (string function in report.ExtensionFunctions)
                text.AppendLine(function);
        text.AppendLine();
        text.AppendLine("Unresolved project-specific call candidates");
        text.AppendLine("-------------------------------------------");
        if (report.UnresolvedFunctions.Count == 0)
            text.AppendLine("(None found by the conservative static scan.)");
        else
            foreach (string function in report.UnresolvedFunctions)
                text.AppendLine(function);
        text.AppendLine();
        text.AppendLine("These are candidates, not guaranteed compile errors. GameMaker built-ins are deliberately filtered conservatively.");
        File.WriteAllText(path, text.ToString(), Utf8NoBom);
    }

    private static void WriteTextReport(string path, ReconstructionRepairReport report)
    {
        StringBuilder text = new();
        text.AppendLine("SplitGM automatic reconstructed-project repair report");
        text.AppendLine("=====================================================");
        text.AppendLine($"Generator: {SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}");
        text.AppendLine($"Generated UTC: {report.GeneratedAtUtc:O}");
        text.AppendLine($"Project: {report.ProjectFile}");
        text.AppendLine($"Target profile: {report.TargetProfile}");
        text.AppendLine($"Original pre-repair mirror: {report.OriginalOutputDirectory}");
        text.AppendLine($"Repairs applied: {report.AppliedCount:N0}");
        text.AppendLine($"Certain/high-confidence repairs: {report.CertainOrHighConfidenceCount:N0}");
        text.AppendLine($"Manual-review items: {report.ManualReviewCount:N0}");
        text.AppendLine();
        text.AppendLine("Static compile preflight");
        text.AppendLine("------------------------");
        text.AppendLine($"Passed: {report.Preflight.Passed}");
        text.AppendLine($"JSON files checked / parse errors: {report.Preflight.JsonFilesChecked:N0} / {report.Preflight.JsonParseErrors:N0}");
        text.AppendLine($"GML files checked / balance errors: {report.Preflight.GmlFilesChecked:N0} / {report.Preflight.GmlBalanceErrors:N0}");
        text.AppendLine($"Missing files: {report.Preflight.MissingFiles:N0}");
        text.AppendLine($"Invalid identifiers: {report.Preflight.InvalidIdentifiers:N0}");
        text.AppendLine($"Duplicate names: {report.Preflight.DuplicateNames:N0}");
        text.AppendLine($"Broken resource references: {report.Preflight.BrokenResourceReferences:N0}");
        text.AppendLine($"Unresolved function candidates: {report.Preflight.UnresolvedFunctionCandidates:N0}");
        text.AppendLine();

        int number = 1;
        foreach (ReconstructionRepairAction action in report.Actions)
        {
            text.AppendLine($"{number++:D4}. [{(action.Applied ? "APPLIED" : "MANUAL")}] [{action.Confidence}] {action.Category}");
            text.AppendLine($"      ID: {action.Id}");
            text.AppendLine($"      {action.Description}");
            if (!string.IsNullOrWhiteSpace(action.RelativePath))
                text.AppendLine($"      Path: {action.RelativePath}");
            if (!string.IsNullOrWhiteSpace(action.Before))
                text.AppendLine($"      Before: {action.Before}");
            if (!string.IsNullOrWhiteSpace(action.After))
                text.AppendLine($"      After: {action.After}");
            if (!string.IsNullOrWhiteSpace(action.Evidence))
                text.AppendLine($"      Evidence: {action.Evidence}");
            foreach (string step in action.ManualSteps)
                text.AppendLine($"      Manual step: {step}");
            text.AppendLine();
        }
        File.WriteAllText(path, text.ToString(), Utf8NoBom);
    }

    private static void EnsurePlaceholderGml(
        string outputDirectory,
        string path,
        string purpose,
        ReconstructionRepairReport report)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "/// @description SplitGM automatic repair placeholder\n" +
            $"// Missing {purpose}; inspect the preserved VM assembly and repair report.\n",
            Utf8NoBom);
        AddApplied(report, "MISSING-CODE-FILE", "Missing object-event, room-code, or instance-code file",
            $"Created a structurally valid placeholder for missing {purpose}.",
            Relative(outputDirectory, path), null, "Placeholder GML", RepairConfidence.Medium,
            "This prevents a missing-file load error, but does not claim to restore behavior that was not recoverable.");
    }

    private static bool SetInt(JsonObject target, string property, int value)
    {
        if (GetInt(target[property], int.MinValue) == value)
            return false;
        target[property] = value;
        return true;
    }

    private static bool SetDefault(JsonObject target, string property, JsonNode? value)
    {
        if (target[property] is not null)
            return false;
        target[property] = value?.DeepClone();
        return true;
    }

    private static bool GetBool(JsonNode? node, bool fallback)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out bool boolean))
            return boolean;
        return fallback;
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return text;
        return null;
    }

    private static int GetInt(JsonNode? node, int fallback)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out int integer))
            return integer;
        if (value.TryGetValue(out long longValue))
            return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
        if (value.TryGetValue(out double doubleValue) && double.IsFinite(doubleValue))
            return (int)Math.Clamp(Math.Truncate(doubleValue), int.MinValue, int.MaxValue);
        if (value.TryGetValue(out decimal decimalValue))
            return (int)Math.Clamp(decimal.Truncate(decimalValue), int.MinValue, int.MaxValue);
        return fallback;
    }

    private static double GetDouble(JsonNode? node, double fallback)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue(out double doubleValue) && double.IsFinite(doubleValue))
            return doubleValue;
        if (value.TryGetValue(out float floatValue) && float.IsFinite(floatValue))
            return floatValue;
        if (value.TryGetValue(out long longValue))
            return longValue;
        if (value.TryGetValue(out decimal decimalValue))
            return (double)decimalValue;
        return fallback;
    }

    private static string InferResourceType(string path, string? current)
    {
        if (!string.IsNullOrWhiteSpace(current))
            return current;
        if (Path.GetExtension(path).Equals(".yyp", StringComparison.OrdinalIgnoreCase))
            return "GMProject";
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("/scripts/")) return "GMScript";
        if (normalized.Contains("/objects/")) return "GMObject";
        if (normalized.Contains("/rooms/")) return "GMRoom";
        if (normalized.Contains("/sprites/")) return "GMSprite";
        if (normalized.Contains("/sounds/")) return "GMSound";
        if (normalized.Contains("/paths/")) return "GMPath";
        if (normalized.Contains("/folders/")) return "GMFolder";
        return string.Empty;
    }

    private static string TagForResourceType(string type) => type switch
    {
        "GMProject" => "$GMProject",
        "GMScript" => "$GMScript",
        "GMObject" => "$GMObject",
        "GMRoom" => "$GMRoom",
        "GMSprite" => "$GMSprite",
        "GMSound" => "$GMSound",
        "GMPath" => "$GMPath",
        "GMFolder" => "$GMFolder",
        _ => string.Empty
    };

    private static string EventStem(int type) => type switch
    {
        0 => "Create",
        1 => "Destroy",
        2 => "Alarm",
        3 => "Step",
        4 => "Collision",
        5 => "Keyboard",
        6 => "Mouse",
        7 => "Other",
        8 => "Draw",
        9 => "KeyPress",
        10 => "KeyRelease",
        11 => "Trigger",
        12 => "CleanUp",
        13 => "Gesture",
        14 => "PreCreate",
        _ => "Event" + type.ToString(CultureInfo.InvariantCulture)
    };

    private static string ExtractDeclarationIdentifier(string declaration)
    {
        int equals = declaration.IndexOf('=');
        string left = (equals >= 0 ? declaration[..equals] : declaration).Trim();
        int whitespace = left.IndexOfAny([' ', '\t']);
        if (whitespace >= 0)
            left = left[..whitespace];
        return IdentifierRegex.IsMatch(left) ? left : string.Empty;
    }

    private static List<string> SplitCommaAware(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        List<string> items = [];
        int start = 0;
        int depth = 0;
        bool inString = false;
        char quote = '\0';
        bool escape = false;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == quote) inString = false;
                continue;
            }
            if (c is '\'' or '"')
            {
                inString = true;
                quote = c;
            }
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                items.Add(text[start..index].Trim());
                start = index + 1;
            }
        }
        items.Add(text[start..].Trim());
        return items;
    }

    private static IEnumerable<JsonObject> EnumerateObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach ((_, JsonNode? child) in obj)
            {
                if (child is null) continue;
                foreach (JsonObject nested in EnumerateObjects(child))
                    yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is null) continue;
                foreach (JsonObject nested in EnumerateObjects(child))
                    yield return nested;
            }
        }
    }

    private static JsonObject? TryReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? ResolveCaseInsensitiveRelativePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        string normalized = relative.Replace('\\', '/').TrimStart('/');
        string direct = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct) || Directory.Exists(direct))
            return normalized;

        string current = root;
        List<string> resolved = [];
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
                return null;
            string? match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => Path.GetFileName(entry).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            resolved.Add(Path.GetFileName(match));
            current = match;
        }
        return string.Join('/', resolved);
    }

    private static IReadOnlyList<GmlFunctionScope> FindFunctionScopes(
        string text,
        MatchCollection headers)
    {
        if (headers.Count == 0)
            return [];

        string stripped = StripCommentsAndStrings(text);
        List<GmlFunctionScope> scopes = [];
        for (int index = 0; index < headers.Count; index++)
        {
            Match header = headers[index];
            int searchStart = header.Index + header.Length;
            int searchEnd = index + 1 < headers.Count ? headers[index + 1].Index : stripped.Length;
            if (searchEnd <= searchStart)
                continue;

            int openBrace = stripped.IndexOf('{', searchStart, searchEnd - searchStart);
            if (openBrace < 0)
                continue;

            int depth = 0;
            int closeBrace = -1;
            for (int position = openBrace; position < stripped.Length; position++)
            {
                if (stripped[position] == '{')
                    depth++;
                else if (stripped[position] == '}' && --depth == 0)
                {
                    closeBrace = position;
                    break;
                }
            }
            if (closeBrace >= 0)
                scopes.Add(new GmlFunctionScope(header, openBrace, closeBrace));
        }
        return scopes;
    }

    private static int FindInnermostFunctionScopeKey(
        IReadOnlyList<GmlFunctionScope> scopes,
        int characterIndex)
    {
        int key = -1;
        int deepestStart = -1;
        foreach (GmlFunctionScope scope in scopes)
        {
            if (characterIndex <= scope.BodyStart || characterIndex >= scope.BodyEnd || scope.BodyStart < deepestStart)
                continue;
            key = scope.Header.Index;
            deepestStart = scope.BodyStart;
        }
        return key;
    }

    private static bool HasBalancedGml(string text)
    {
        string stripped = StripCommentsAndStrings(text);
        int braces = 0, parentheses = 0, brackets = 0;
        foreach (char c in stripped)
        {
            switch (c)
            {
                case '{': braces++; break;
                case '}': if (--braces < 0) return false; break;
                case '(': parentheses++; break;
                case ')': if (--parentheses < 0) return false; break;
                case '[': brackets++; break;
                case ']': if (--brackets < 0) return false; break;
            }
        }
        return braces == 0 && parentheses == 0 && brackets == 0;
    }

    private static string StripCommentsAndStrings(string text)
    {
        StringBuilder output = new(text.Length);
        bool lineComment = false, blockComment = false, inString = false, escape = false;
        char quote = '\0';
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (lineComment)
            {
                if (c == '\n') { lineComment = false; output.Append('\n'); }
                else output.Append(' ');
            }
            else if (blockComment)
            {
                if (c == '*' && next == '/') { output.Append("  "); index++; blockComment = false; }
                else output.Append(c == '\n' ? '\n' : ' ');
            }
            else if (inString)
            {
                output.Append(c == '\n' ? '\n' : ' ');
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == quote) inString = false;
            }
            else if (c == '/' && next == '/')
            {
                output.Append("  "); index++; lineComment = true;
            }
            else if (c == '/' && next == '*')
            {
                output.Append("  "); index++; blockComment = true;
            }
            else if (c is '\'' or '"')
            {
                output.Append(' '); inString = true; quote = c;
            }
            else output.Append(c);
        }
        return output.ToString();
    }

    private static bool IsVirtualProjectReference(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("audiogroups/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("texturegroups/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("configs/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyBuiltin(string name)
    {
        string[] prefixes =
        [
            "array_", "buffer_", "camera_", "collision_", "date_", "display_", "draw_", "ds_", "effect_",
            "event_", "external_", "file_", "font_", "gamepad_", "gpu_", "http_", "instance_", "irandom",
            "keyboard_", "layer_", "matrix_", "mouse_", "mp_", "network_", "os_", "path_", "physics_",
            "point_", "random", "room_", "script_", "shader_", "show_", "sprite_", "string_", "surface_",
            "texture_", "tilemap_", "timeline_", "variable_", "vertex_", "window_", "audio_", "steam_"
        ];
        if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;
        return name is "abs" or "arccos" or "arcsin" or "arctan" or "arctan2" or "ceil" or "clamp" or
            "choose" or "cos" or "degtorad" or "exp" or "floor" or "frac" or "is_array" or "is_bool" or
            "is_method" or "is_nan" or "is_numeric" or "is_real" or "is_string" or "lengthdir_x" or
            "lengthdir_y" or "lerp" or "ln" or "log2" or "log10" or "max" or "mean" or "median" or
            "method" or "min" or "power" or "radtodeg" or "round" or "sign" or "sin" or "sqr" or "sqrt" or
            "tan" or "undefined";
    }

    private static bool IsProjectSpecificFunctionCandidate(string name) =>
        name.StartsWith("gml_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("scr_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ext_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("fn_", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("__", StringComparison.Ordinal);

    private static string NormalizeText(string text) => text
        .TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static bool JsonEquivalentFormatting(string original, string normalized)
    {
        string trimmed = original.TrimStart('\uFEFF').Trim();
        return string.Equals(trimmed, normalized.Trim(), StringComparison.Ordinal);
    }

    private static string SafeIdentifier(string value, string fallback)
    {
        StringBuilder output = new();
        foreach (char c in value)
            output.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string result = output.ToString().Trim('_');
        if (result.Length == 0) result = fallback;
        if (char.IsDigit(result[0])) result = "_" + result;
        return result;
    }

    private static bool IsInside(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool IsGeneratedRepairReport(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("SplitGM-Repair-Report", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SplitGM-Unresolved-Functions.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), Utf8NoBom);
    }

    private static void AddApplied(
        ReconstructionRepairReport report,
        string id,
        string category,
        string description,
        string? path,
        string? before,
        string? after,
        RepairConfidence confidence,
        string? evidence)
    {
        report.Actions.Add(new ReconstructionRepairAction
        {
            Id = id,
            Category = category,
            Description = description,
            RelativePath = path,
            Before = before,
            After = after,
            Confidence = confidence,
            Applied = true,
            Evidence = evidence
        });
    }

    private static void AddManual(
        ReconstructionRepairReport report,
        string id,
        string category,
        string description,
        string? path,
        RepairConfidence confidence,
        params string[] manualSteps)
    {
        report.Actions.Add(new ReconstructionRepairAction
        {
            Id = id,
            Category = category,
            Description = description,
            RelativePath = path,
            Confidence = confidence,
            Applied = false,
            ManualSteps = manualSteps.Where(step => !string.IsNullOrWhiteSpace(step)).ToList()
        });
    }

    private static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            value = new TValue();
            dictionary.Add(key, value);
        }
        return value;
    }
}
