namespace SplitGM.Core;

internal sealed class ResolvedGameInput : IDisposable
{
    public required string OriginalPath { get; init; }
    public required string DataPath { get; init; }
    public required string ResolutionMethod { get; init; }
    public bool DeleteOnDispose { get; init; }

    public void Dispose()
    {
        if (!DeleteOnDispose)
            return;

        try
        {
            if (File.Exists(DataPath))
                File.Delete(DataPath);
        }
        catch
        {
            // Temporary cleanup failure must not hide the decompilation result.
        }
    }
}
