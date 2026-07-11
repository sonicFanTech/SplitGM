using System.Collections;
using System.Reflection;
using System.Text;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace SplitGM.Core;

internal static class ObjectInspector
{
    public static string Format(object? value, string? heading = null)
    {
        StringBuilder output = new();
        if (!string.IsNullOrWhiteSpace(heading))
        {
            output.AppendLine(heading);
            output.AppendLine(new string('=', Math.Min(heading.Length, 72)));
        }

        if (value is null)
        {
            output.AppendLine("(null)");
            return output.ToString();
        }

        Type type = value.GetType();
        output.AppendLine($"Type: {type.FullName}");
        output.AppendLine();

        PropertyInfo[] properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int written = 0;
        foreach (PropertyInfo property in properties)
        {
            if (written >= 100)
            {
                output.AppendLine("... additional properties omitted ...");
                break;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception exception)
            {
                output.AppendLine($"{property.Name}: <unavailable: {exception.Message}>");
                written++;
                continue;
            }

            if (!TryFormatScalar(propertyValue, out string formatted))
                continue;

            output.AppendLine($"{property.Name}: {formatted}");
            written++;
        }

        FieldInfo[] fields = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (FieldInfo field in fields)
        {
            if (written >= 100)
            {
                output.AppendLine("... additional members omitted ...");
                break;
            }

            object? fieldValue;
            try
            {
                fieldValue = field.GetValue(value);
            }
            catch (Exception exception)
            {
                output.AppendLine($"{field.Name}: <unavailable: {exception.Message}>");
                written++;
                continue;
            }

            if (!TryFormatScalar(fieldValue, out string formatted))
                continue;

            output.AppendLine($"{field.Name}: {formatted}");
            written++;
        }

        if (written == 0)
            output.AppendLine(value.ToString() ?? "(no printable public members)");

        return output.ToString();
    }


    public static string FormatTree(
        object? value,
        string? heading = null,
        CancellationToken cancellationToken = default,
        int maximumDepth = 3,
        int maximumCollectionItems = 200,
        int maximumCharacters = 1_000_000)
    {
        StringBuilder output = new();
        if (!string.IsNullOrWhiteSpace(heading))
        {
            output.AppendLine(heading);
            output.AppendLine(new string('=', Math.Min(heading.Length, 72)));
        }

        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        AppendValue(value, "Resource", 0);
        return output.ToString();

        void AppendValue(object? current, string label, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length >= maximumCharacters)
                return;

            string indent = new(' ', depth * 2);
            if (current is null)
            {
                output.Append(indent).Append(label).AppendLine(": (null)");
                return;
            }

            Type type = current.GetType();
            if (current is IEnumerable enumerable && current is not string && current is not byte[])
            {
                output.Append(indent).Append(label).Append(": ").AppendLine(type.FullName ?? type.Name);
                if (depth >= maximumDepth)
                {
                    output.Append(indent).AppendLine("  ... maximum inspection depth reached ...");
                    return;
                }
                if (!type.IsValueType && !visited.Add(current))
                {
                    output.Append(indent).AppendLine("  (reference already displayed)");
                    return;
                }

                int index = 0;
                foreach (object? item in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index >= maximumCollectionItems || output.Length >= maximumCharacters)
                    {
                        output.Append(indent).AppendLine("  ... additional collection items omitted ...");
                        break;
                    }
                    AppendValue(item, $"[{index}]", depth + 1);
                    index++;
                }
                return;
            }

            if (TryFormatScalar(current, out string scalar))
            {
                output.Append(indent).Append(label).Append(": ").AppendLine(scalar);
                return;
            }

            output.Append(indent).Append(label).Append(": ").AppendLine(type.FullName ?? type.Name);
            if (depth >= maximumDepth)
            {
                output.Append(indent).AppendLine("  ... maximum inspection depth reached ...");
                return;
            }

            if (!type.IsValueType && !visited.Add(current))
            {
                output.Append(indent).AppendLine("  (reference already displayed)");
                return;
            }

            int written = 0;
            foreach (PropertyInfo property in type
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.GetIndexParameters().Length == 0)
                         .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (written >= 100 || output.Length >= maximumCharacters)
                {
                    output.Append(indent).AppendLine("  ... additional properties omitted ...");
                    break;
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(current);
                }
                catch (Exception exception)
                {
                    output.Append(indent).Append("  ").Append(property.Name)
                        .Append(": <unavailable: ").Append(exception.Message).AppendLine(">");
                    written++;
                    continue;
                }

                AppendValue(propertyValue, property.Name, depth + 1);
                written++;
            }
        }
    }

    private static bool TryFormatScalar(object? value, out string formatted)
    {
        if (value is null)
        {
            formatted = "(null)";
            return true;
        }

        switch (value)
        {
            case string text:
                formatted = QuoteAndTrim(text);
                return true;
            case UndertaleString undertaleString:
                formatted = QuoteAndTrim(undertaleString.Content ?? string.Empty);
                return true;
            case Enum:
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
            case Guid:
            case DateTime:
            case DateTimeOffset:
                formatted = value.ToString() ?? string.Empty;
                return true;
            case byte[] bytes:
                formatted = $"byte[{bytes.Length}]";
                return true;
            case ICollection collection:
                formatted = $"{value.GetType().Name} (Count = {collection.Count})";
                return true;
            case UndertaleNamedResource namedResource:
                formatted = namedResource.Name?.Content ?? namedResource.ToString() ?? "(unnamed resource)";
                return true;
            default:
                formatted = string.Empty;
                return false;
        }
    }

    private static string QuoteAndTrim(string text)
    {
        string escaped = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        if (escaped.Length > 500)
            escaped = escaped[..500] + "...";

        return $"\"{escaped}\"";
    }
}
