#nullable enable

using NAudio.Vorbis;
using NAudio.Wave;
using SplitGM.Core;

namespace SplitGM.Gui;

internal sealed class AudioPreviewPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private MemoryStream? _stream;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public void Play(AudioPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Stop();

        _stream = new MemoryStream(payload.Data, writable: false);
        _reader = payload.Extension.ToLowerInvariant() switch
        {
            ".wav" => new WaveFileReader(_stream),
            ".ogg" => new VorbisWaveReader(_stream),
            ".mp3" => new Mp3FileReader(_stream),
            _ => throw new NotSupportedException(
                $"Audio preview supports WAV, OGG Vorbis, and MP3. This resource is {payload.Format}.")
        };

        _output = new WaveOutEvent { DeviceNumber = 0 };
        _output.PlaybackStopped += Output_PlaybackStopped;
        _output.Init(_reader);
        _output.Play();
    }

    public void Stop()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            try
            {
                _output.Stop();
            }
            catch
            {
                // Device teardown errors should not prevent the selected resource from changing.
            }
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Dispose on the UI caller's next interaction. Calling Stop synchronously from this
        // callback can race NAudio's playback thread on a few older Windows audio drivers.
    }

    public void Dispose()
    {
        Stop();
    }
}
