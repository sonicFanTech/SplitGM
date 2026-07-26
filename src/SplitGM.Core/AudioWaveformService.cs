using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SplitGM.Core;

/// <summary>
/// Decodes actual audio samples and reduces them to a bounded min/max waveform.
/// The waveform is suitable for the interactive viewer and for deterministic SVG/
/// JSON export without transcoding or changing the original audio asset.
/// </summary>
internal static class AudioWaveformService
{
    private const int DefaultPointCount = 2048;
    private const long MaximumFramesToInspect = 250_000_000;

    public static AudioWaveformInfo Analyze(
        byte[] data,
        string format,
        CancellationToken cancellationToken,
        int pointCount = DefaultPointCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new InvalidDataException("The audio payload is empty.");

        pointCount = Math.Clamp(pointCount, 256, 8192);
        using WaveStream reader = CreateReader(data, format, out string decoder);
        ISampleProvider samples = reader.ToSampleProvider();
        int channels = samples.WaveFormat.Channels;
        int sampleRate = samples.WaveFormat.SampleRate;
        if (channels is < 1 or > 1024)
            throw new InvalidDataException($"The decoder reported an unsafe channel count: {channels}.");
        if (sampleRate is < 1 or > 2_000_000)
            throw new InvalidDataException($"The decoder reported an unsafe sample rate: {sampleRate} Hz.");

        double reportedSeconds = reader.TotalTime.TotalSeconds;
        double estimatedFrameCount = Math.Ceiling(reportedSeconds * sampleRate);
        long estimatedFrames = double.IsFinite(estimatedFrameCount) && estimatedFrameCount > 0
            ? estimatedFrameCount >= long.MaxValue
                ? long.MaxValue
                : Math.Max(1L, (long)estimatedFrameCount)
            : pointCount;

        float[] minima = Enumerable.Repeat(1.0f, pointCount).ToArray();
        float[] maxima = Enumerable.Repeat(-1.0f, pointCount).ToArray();
        bool[] touched = new bool[pointCount];
        int bufferFrames = Math.Max(1, 16_384 / channels);
        float[] buffer = new float[bufferFrames * channels];

        long framePosition = 0;
        long samplesRead = 0;
        double sumSquares = 0;
        float absolutePeak = 0;
        bool complete = true;

        while (framePosition < MaximumFramesToInspect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = samples.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            int completeSampleCount = read - (read % channels);
            for (int sampleIndex = 0; sampleIndex < completeSampleCount; sampleIndex += channels)
            {
                float frameMin = 1.0f;
                float frameMax = -1.0f;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = Math.Clamp(buffer[sampleIndex + channel], -1.0f, 1.0f);
                    frameMin = Math.Min(frameMin, value);
                    frameMax = Math.Max(frameMax, value);
                    absolutePeak = Math.Max(absolutePeak, Math.Abs(value));
                    sumSquares += value * value;
                    samplesRead++;
                }

                int bucket = (int)Math.Min(
                    pointCount - 1L,
                    framePosition * pointCount / Math.Max(estimatedFrames, 1L));
                minima[bucket] = Math.Min(minima[bucket], frameMin);
                maxima[bucket] = Math.Max(maxima[bucket], frameMax);
                touched[bucket] = true;
                framePosition++;

                if (framePosition >= MaximumFramesToInspect)
                {
                    complete = false;
                    break;
                }
            }
        }

        // Some compressed readers report a slightly inaccurate duration. If frames
        // spilled into the final bucket, preserve them; untouched gaps are filled by
        // linear carry-forward/backfill so the display never contains invalid spikes.
        FillUntouchedBuckets(minima, maxima, touched);

        double durationSeconds = framePosition / (double)sampleRate;
        double rms = samplesRead == 0 ? 0 : Math.Sqrt(sumSquares / samplesRead);
        return new AudioWaveformInfo(
            minima,
            maxima,
            durationSeconds,
            sampleRate,
            channels,
            framePosition,
            absolutePeak,
            rms,
            complete,
            decoder);
    }

    public static string ToSvg(AudioWaveformInfo waveform, int width = 1600, int height = 420)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        width = Math.Clamp(width, 320, 8192);
        height = Math.Clamp(height, 120, 2160);

        int count = waveform.PointCount;
        if (count == 0)
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\"/>";

        double center = height / 2.0;
        double amplitude = Math.Max(1, height * 0.43);
        StringBuilder path = new(count * 28);

        for (int index = 0; index < count; index++)
        {
            double x = count == 1 ? 0 : index * (width - 1.0) / (count - 1.0);
            double y = center - waveform.MaximumPeaks[index] * amplitude;
            path.Append(index == 0 ? 'M' : 'L')
                .Append(x.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
                .Append(y.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
        }
        for (int index = count - 1; index >= 0; index--)
        {
            double x = count == 1 ? 0 : index * (width - 1.0) / (count - 1.0);
            double y = center - waveform.MinimumPeaks[index] * amplitude;
            path.Append('L')
                .Append(x.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
                .Append(y.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
        }
        path.Append('Z');

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <rect width="100%" height="100%" fill="#06090e"/>
              <line x1="0" y1="{center.ToString("0.###", CultureInfo.InvariantCulture)}" x2="{width}" y2="{center.ToString("0.###", CultureInfo.InvariantCulture)}" stroke="#27364a" stroke-width="1"/>
              <path d="{path}" fill="#20d8ff" fill-opacity="0.78" stroke="#8df1ff" stroke-width="1"/>
            </svg>
            """;
    }

    public static string ToJson(AudioWaveformInfo waveform)
    {
        return JsonSerializer.Serialize(waveform, new JsonSerializerOptions { WriteIndented = true });
    }

    private static WaveStream CreateReader(byte[] data, string format, out string decoder)
    {
        string normalized = format.Trim().ToUpperInvariant();
        List<Exception> failures = [];

        foreach (Func<WaveStream> factory in CandidateFactories(data, normalized))
        {
            try
            {
                WaveStream reader = factory();
                decoder = reader.GetType().Name;
                return reader;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception);
            }
        }

        throw new InvalidDataException(
            $"NAudio could not decode the {format} payload. " +
            string.Join(" | ", failures.Take(3).Select(item => item.Message)));
    }

    private static IEnumerable<Func<WaveStream>> CandidateFactories(byte[] data, string normalizedFormat)
    {
        Func<WaveStream> wav = () => new WaveFileReader(new MemoryStream(data, writable: false));
        Func<WaveStream> ogg = () => new VorbisWaveReader(new MemoryStream(data, writable: false));
        Func<WaveStream> mp3 = () => new Mp3FileReader(new MemoryStream(data, writable: false));

        if (normalizedFormat.Contains("OGG", StringComparison.Ordinal))
        {
            yield return ogg;
            yield return wav;
            yield return mp3;
        }
        else if (normalizedFormat.Contains("MP3", StringComparison.Ordinal))
        {
            yield return mp3;
            yield return wav;
            yield return ogg;
        }
        else
        {
            yield return wav;
            yield return ogg;
            yield return mp3;
        }
    }

    private static void FillUntouchedBuckets(float[] minima, float[] maxima, bool[] touched)
    {
        int previous = -1;
        for (int index = 0; index < touched.Length; index++)
        {
            if (touched[index])
            {
                previous = index;
                continue;
            }

            if (previous >= 0)
            {
                minima[index] = minima[previous];
                maxima[index] = maxima[previous];
            }
        }

        int next = -1;
        for (int index = touched.Length - 1; index >= 0; index--)
        {
            if (touched[index])
            {
                next = index;
                continue;
            }

            if (next >= 0)
            {
                minima[index] = minima[next];
                maxima[index] = maxima[next];
            }
            else if (previous < 0)
            {
                minima[index] = 0;
                maxima[index] = 0;
            }
        }
    }
}
