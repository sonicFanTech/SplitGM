using System.Buffers.Binary;
using System.Text;

namespace SplitGM.Core;

internal static class InputResolver
{
    private static readonly HashSet<string> DirectDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".win", ".unx", ".ios", ".droid", ".android", ".game"
    };

    public static ResolvedGameInput Resolve(
        string requestedPath,
        Action<LogMessage>? log,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new UnsupportedInputException("No input file was selected.");

        string path = Path.GetFullPath(requestedPath.Trim('"'));
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected input file does not exist.", path);

        cancellationToken.ThrowIfCancellationRequested();

        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);

        if (fileName.Equals("data.win", StringComparison.OrdinalIgnoreCase) ||
            DirectDataExtensions.Contains(extension))
        {
            ValidateFormHeader(path);
            return new ResolvedGameInput
            {
                OriginalPath = path,
                DataPath = path,
                ResolutionMethod = "Direct GameMaker data file"
            };
        }

        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedInputException(
                "Unsupported input. Select data.win, a GameMaker platform data file, or a Windows GameMaker EXE.");
        }

        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        foreach (string candidateName in new[] { "data.win", Path.GetFileNameWithoutExtension(path) + ".win" })
        {
            string sidecar = Path.Combine(directory, candidateName);
            if (!File.Exists(sidecar))
                continue;

            log?.Invoke(LogMessage.Info($"Found neighboring GameMaker data file: {sidecar}"));
            ValidateFormHeader(sidecar);
            return new ResolvedGameInput
            {
                OriginalPath = path,
                DataPath = sidecar,
                ResolutionMethod = "Neighboring GameMaker data file"
            };
        }

        log?.Invoke(LogMessage.Info("No neighboring data.win was found. Scanning the EXE for an embedded FORM archive..."));
        (long offset, long length) = FindEmbeddedForm(path, cancellationToken);

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"SplitGM_{Guid.NewGuid():N}.data.win");

        ExtractRange(path, tempPath, offset, length, cancellationToken);
        ValidateFormHeader(tempPath);

        log?.Invoke(LogMessage.Success(
            $"Extracted an embedded GameMaker archive from EXE offset 0x{offset:X}."));

        return new ResolvedGameInput
        {
            OriginalPath = path,
            DataPath = tempPath,
            ResolutionMethod = $"Embedded FORM archive at EXE offset 0x{offset:X}",
            DeleteOnDispose = true
        };
    }

    private static void ValidateFormHeader(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("FORM"u8))
            throw new UnsupportedInputException("The selected file does not begin with a valid GameMaker FORM header.");

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        long expectedLength = 8L + payloadLength;
        if (payloadLength < 8 || expectedLength > stream.Length)
            throw new UnsupportedInputException("The GameMaker FORM length is invalid or truncated.");
    }

    private static (long Offset, long Length) FindEmbeddedForm(
        string exePath,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[bufferSize + 7];
        int carry = 0;
        long absoluteStart = 0;

        using FileStream stream = File.Open(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, carry, bufferSize);
            if (read == 0)
                break;

            int available = carry + read;
            for (int i = 0; i <= available - 8; i++)
            {
                if (buffer[i] != (byte)'F' || buffer[i + 1] != (byte)'O' ||
                    buffer[i + 2] != (byte)'R' || buffer[i + 3] != (byte)'M')
                    continue;

                long candidateOffset = absoluteStart - carry + i;
                uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 4, 4));
                long totalLength = 8L + payloadLength;

                if (payloadLength < 16 || candidateOffset < 0 ||
                    candidateOffset + totalLength > stream.Length)
                    continue;

                if (LooksLikeGameMakerForm(stream, candidateOffset, totalLength))
                    return (candidateOffset, totalLength);
            }

            carry = Math.Min(7, available);
            Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
            absoluteStart += read;
        }

        throw new UnsupportedInputException(
            "No validated embedded GameMaker FORM archive was found in the EXE. The game may use a separate data file, a packed runner, YYC-only packaging, or an unsupported format.");
    }

    private static bool LooksLikeGameMakerForm(FileStream stream, long offset, long totalLength)
    {
        long oldPosition = stream.Position;
        try
        {
            stream.Position = offset + 8;
            Span<byte> chunkHeader = stackalloc byte[8];
            if (stream.Read(chunkHeader) != chunkHeader.Length)
                return false;

            string firstChunk = Encoding.ASCII.GetString(chunkHeader[..4]);
            uint firstLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            return firstChunk == "GEN8" && firstLength > 0 && 16L + firstLength <= totalLength;
        }
        finally
        {
            stream.Position = oldPosition;
        }
    }

    private static void ExtractRange(
        string sourcePath,
        string destinationPath,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        byte[] buffer = new byte[bufferSize];

        using FileStream source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream destination = File.Create(destinationPath);
        source.Position = offset;

        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read <= 0)
                throw new EndOfStreamException("The embedded GameMaker archive ended unexpectedly.");

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
