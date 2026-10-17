using System.Media;
using Murmur.Core.Interfaces;

namespace Murmur.Core.Services;

/// <summary>
/// Generates and plays subtle, synthesized tactile audio feedback for dictation events.
/// Pure in-memory generation with zero external asset dependencies.
/// </summary>
public class SoundEffectService : ISoundEffectService
{
    private const int SampleRate = 44100;
    private readonly ISettingsManager _settingsManager;

    public SoundEffectService(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
    }

    public void PlayRecordingStart()
    {
        // Rising subtle chime: 480 Hz -> 660 Hz over 90ms
        PlayToneAsync(sampleCount =>
        {
            var samples = new short[sampleCount];
            var duration = (double)sampleCount / SampleRate;
            for (int i = 0; i < sampleCount; i++)
            {
                var t = (double)i / sampleCount;
                var freq = 480.0 + (180.0 * t);
                var phase = 2.0 * Math.PI * freq * (i / (double)SampleRate);
                var env = Envelope(i, sampleCount, attackSamples: 400, decaySamples: 800);
                samples[i] = (short)(Math.Sin(phase) * env * 32767.0);
            }
            return samples;
        }, durationMs: 90);
    }

    public void PlayDictationSuccess()
    {
        // Two-tone pleasant confirmation chime: 784 Hz (G5) -> 1046 Hz (C6) over 130ms
        PlayToneAsync(sampleCount =>
        {
            var samples = new short[sampleCount];
            var split = (int)(sampleCount * 0.45);
            for (int i = 0; i < sampleCount; i++)
            {
                double freq = i < split ? 784.0 : 1046.5;
                var phase = 2.0 * Math.PI * freq * (i / (double)SampleRate);
                var env = Envelope(i, sampleCount, attackSamples: 300, decaySamples: 1200);
                samples[i] = (short)(Math.Sin(phase) * env * 32767.0);
            }
            return samples;
        }, durationMs: 130);
    }

    public void PlayError()
    {
        // Descending low cue: 320 Hz -> 220 Hz over 130ms
        PlayToneAsync(sampleCount =>
        {
            var samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                var t = (double)i / sampleCount;
                var freq = 320.0 - (100.0 * t);
                var phase = 2.0 * Math.PI * freq * (i / (double)SampleRate);
                var env = Envelope(i, sampleCount, attackSamples: 400, decaySamples: 1000);
                samples[i] = (short)(Math.Sin(phase) * env * 32767.0);
            }
            return samples;
        }, durationMs: 130);
    }

    private void PlayToneAsync(Func<int, short[]> generator, int durationMs)
    {
        try
        {
            var settings = _settingsManager.Load();
            if (!settings.PlayAlertSounds || settings.SoundVolume <= 0)
            {
                return;
            }

            var volumeScale = Math.Clamp(settings.SoundVolume / 100.0, 0.0, 1.0) * 0.35; // Cap at 35% max amplitude for pleasant subtlety

            Task.Run(() =>
            {
                try
                {
                    var sampleCount = (int)(SampleRate * (durationMs / 1000.0));
                    var rawSamples = generator(sampleCount);

                    // Scale by volume
                    for (int i = 0; i < rawSamples.Length; i++)
                    {
                        rawSamples[i] = (short)(rawSamples[i] * volumeScale);
                    }

                    var wavBytes = CreateWavBytes(rawSamples, SampleRate);
                    using var ms = new MemoryStream(wavBytes);
                    using var player = new SoundPlayer(ms);
                    player.Play();
                }
                catch
                {
                    // Audio feedback is best-effort and must never crash app
                }
            });
        }
        catch
        {
            // Best effort
        }
    }

    private static double Envelope(int index, int total, int attackSamples, int decaySamples)
    {
        if (index < attackSamples)
        {
            return (double)index / attackSamples;
        }

        var remaining = total - index;
        if (remaining < decaySamples)
        {
            return (double)remaining / decaySamples;
        }

        return 1.0;
    }

    private static byte[] CreateWavBytes(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + samples.Length * 2);
        bw.Write("WAVE"u8);

        // Subchunk 1: fmt
        bw.Write("fmt "u8);
        bw.Write(16);                 // Subchunk1Size
        bw.Write((short)1);           // PCM
        bw.Write((short)1);           // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);     // Byte rate (sampleRate * numChannels * bitsPerSample/8)
        bw.Write((short)2);           // Block align
        bw.Write((short)16);          // Bits per sample

        // Subchunk 2: data
        bw.Write("data"u8);
        bw.Write(samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }

        return ms.ToArray();
    }
}
