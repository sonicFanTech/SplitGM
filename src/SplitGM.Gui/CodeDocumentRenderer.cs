#nullable enable

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Windows.Media;

namespace SplitGM.Gui;

internal enum CodeDocumentMode
{
    Plain,
    Gml,
    Assembly
}

internal static class CodeDocumentRenderer
{
    private static string? _highlightingLoadError;

    public static string? HighlightingLoadError => _highlightingLoadError;
    private static readonly Lazy<IHighlightingDefinition?> GmlHighlighting =
        new(() => LoadHighlighting("SplitGM.Gui.Resources.GML.xshd"));

    private static readonly Lazy<IHighlightingDefinition?> AssemblyHighlighting =
        new(() => LoadHighlighting("SplitGM.Gui.Resources.VMASM.xshd"));

    public static void Configure(TextEditor editor, CodeDocumentMode mode)
    {
        editor.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        editor.FontSize = 13;
        editor.IsReadOnly = true;
        editor.ShowLineNumbers = true;
        editor.WordWrap = false;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.TextArea.TextView.Options.HighlightCurrentLine = true;
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromRgb(18, 31, 45));
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        editor.SyntaxHighlighting = mode switch
        {
            CodeDocumentMode.Gml => GmlHighlighting.Value,
            CodeDocumentMode.Assembly => AssemblyHighlighting.Value,
            _ => null
        };
    }

    public static void SetText(
        TextEditor editor,
        string? text,
        CodeDocumentMode mode,
        bool includeLineNumbers)
    {
        text ??= string.Empty;
        IHighlightingDefinition? desiredHighlighting = mode switch
        {
            CodeDocumentMode.Gml => GmlHighlighting.Value,
            CodeDocumentMode.Assembly => AssemblyHighlighting.Value,
            _ => null
        };
        // AvalonEdit is dramatically lighter than a RichTextBox with one Run per token. For
        // exceptionally large outputs, disabling live highlighting avoids the remaining parser cost.
        editor.SyntaxHighlighting = text.Length <= 2_000_000 ? desiredHighlighting : null;

        int oldOffset = editor.CaretOffset;
        int oldLine = editor.Document.TextLength > 0
            ? editor.Document.GetLineByOffset(Math.Min(oldOffset, editor.Document.TextLength)).LineNumber
            : 1;
        bool sameDocument = editor.Document.TextLength > 0;

        editor.Document.BeginUpdate();
        try
        {
            editor.Document.Text = text;
        }
        finally
        {
            editor.Document.EndUpdate();
        }

        editor.ShowLineNumbers = includeLineNumbers;
        editor.Document.UndoStack.ClearAll();

        if (sameDocument && oldOffset <= editor.Document.TextLength)
        {
            editor.CaretOffset = oldOffset;
            editor.ScrollToLine(Math.Min(oldLine, editor.Document.LineCount));
        }
        else
        {
            editor.CaretOffset = 0;
            editor.ScrollTo(1, 1);
        }
    }

    public static void SetLoadingText(TextEditor editor, string message, CodeDocumentMode mode)
    {
        SetText(editor, message, mode, editor.ShowLineNumbers);
    }

    private static IHighlightingDefinition? LoadHighlighting(string resourceName)
    {
        try
        {
            Assembly assembly = typeof(CodeDocumentRenderer).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                _highlightingLoadError = $"Embedded highlighting resource was not found: {resourceName}";
                return null;
            }

            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true
            });
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            // Syntax coloring is optional. A damaged or version-incompatible XSHD must
            // never prevent the resource viewer itself from starting.
            _highlightingLoadError = $"{resourceName}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
