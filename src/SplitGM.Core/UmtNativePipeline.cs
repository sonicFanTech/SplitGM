using Underanalyzer.Decompiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace SplitGM.Core;

/// <summary>
/// Thin, read-only integration layer around the exact UndertaleModTool loading,
/// decompilation, disassembly, texture and archive model APIs used by SplitGM.
/// Keeping these calls in one place prevents SplitGM from drifting into a second,
/// subtly incompatible reimplementation of UMT behavior.
/// </summary>
internal static class UmtNativePipeline
{
    public static UndertaleData ReadGameData(
        Stream stream,
        ICollection<string>? warnings = null,
        Action<LogMessage>? log = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return UndertaleIO.Read(
            stream,
            warningHandler: (message, important) =>
            {
                warnings?.Add(message);
                log?.Invoke(important
                    ? LogMessage.Warning("[UMT important] " + message)
                    : LogMessage.Warning(message));
            },
            messageHandler: message => log?.Invoke(LogMessage.Info(message)));
    }

    public static UndertaleData ReadGameData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return UndertaleIO.Read(stream);
    }

    public static void WriteGameData(
        Stream stream,
        UndertaleData data,
        Action<LogMessage>? log = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(data);

        UndertaleIO.Write(
            stream,
            data,
            message => log?.Invoke(LogMessage.Info(message)));
    }

    public static string DecompileCode(
        UndertaleData data,
        UndertaleCode code,
        GlobalDecompileContext globalContext,
        IDecompileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(globalContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (code.ParentEntry is not null)
        {
            return $"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name?.Content}\"; decompile the parent entry instead.";
        }

        // This is the same Underanalyzer/UndertaleModLib path used by UMT's
        // GetDecompiledText implementation and its parallel debug-source export.
        return new DecompileContext(globalContext, code, settings).DecompileToString();
    }

    public static string DisassembleCode(UndertaleData data, UndertaleCode code)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(code);

        if (code.ParentEntry is not null)
        {
            return $"; This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name?.Content}\"; disassemble the parent entry instead.";
        }

        IList<UndertaleVariable> variables = data.Variables ?? new List<UndertaleVariable>();
        return code.Disassemble(
            variables,
            data.CodeLocals?.For(code),
            ignoreMissingCodeLocals: true);
    }
}
