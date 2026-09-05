using System.Media;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.App.Audio;

/// <summary>
/// The short chime played when a clip is saved — the only confirmation the user gets when the
/// overlay cannot draw (a true fullscreen-exclusive game).
/// <para>
/// It is deliberately <b>not</b> <c>SystemSounds.Asterisk</c>. That call is <c>MessageBeep</c>,
/// which is rendered by Windows' own shared "System Sounds" session rather than by this process —
/// so no per-process exclusion can keep it out of a loopback capture, and the chime ends up audible
/// in the next replay saved within the buffer window. <see cref="SoundPlayer"/> goes through
/// <c>PlaySound</c>, which opens an ordinary render stream <i>inside this process</i>, which is
/// exactly what <see cref="ReplayCapture.Core.Audio.AudioEngine"/>'s own-process exclusion removes
/// from the desktop stems.
/// </para>
/// <para>
/// The waveform is synthesised rather than loaded from <c>%WINDIR%\Media</c>: which .wav files exist
/// there varies by Windows edition and by whatever sound scheme the user has applied, and a cue that
/// silently stops existing is worse than one this app owns outright.
/// </para>
/// </summary>
internal sealed class SaveCue : IDisposable
{
    private const int SampleRate = 44100;

    /// <summary>Peak amplitude, well below full scale — this plays over a game, not instead of one.</summary>
    private const double Amplitude = 0.22;

    private readonly SoundPlayer? _player;

    public SaveCue()
    {
        try
        {
            _player = new SoundPlayer(new MemoryStream(BuildChimeWav()));

            // Decode up front: Play() would otherwise parse the stream on the first save, on the
            // UI thread, at the one moment latency is actually visible.
            _player.Load();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not prepare the save chime ({ex.Message}); falling back to the system sound, " +
                     "which is rendered outside this process and so may be audible in later replays.");
        }
    }

    public void Play()
    {
        try
        {
            if (_player is not null) _player.Play();
            else SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not play the save sound: {ex.Message}");
        }
    }

    public void Dispose() => _player?.Dispose();

    /// <summary>Two short rising blips, as a 16-bit mono PCM RIFF file.</summary>
    private static byte[] BuildChimeWav()
    {
        var samples = new List<short>(SampleRate / 4);
        AppendTone(samples, frequency: 1046.5, milliseconds: 90);    // C6
        AppendTone(samples, frequency: 1567.98, milliseconds: 150);  // G6

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var dataBytes = samples.Count * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                                // PCM chunk size
        writer.Write((short)1);                          // WAVE_FORMAT_PCM
        writer.Write((short)1);                          // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));        // bytes per second
        writer.Write((short)sizeof(short));              // block align
        writer.Write((short)16);                         // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples) writer.Write(sample);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Appends one tone with a short attack and an exponential decay. Both matter: a bare sine
    /// switched on and off at zero-crossing-agnostic boundaries clicks at each end.
    /// </summary>
    private static void AppendTone(List<short> samples, double frequency, int milliseconds)
    {
        var count = SampleRate * milliseconds / 1000;
        var attack = SampleRate / 500;   // 2 ms

        for (var i = 0; i < count; i++)
        {
            var envelope = Math.Min(1.0, (double)i / attack) * Math.Exp(-3.0 * i / count);
            var value = Math.Sin(2 * Math.PI * frequency * i / SampleRate) * envelope * Amplitude;
            samples.Add((short)(value * short.MaxValue));
        }
    }
}
