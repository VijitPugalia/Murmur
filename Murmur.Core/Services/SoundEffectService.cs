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

    private static readonly short[] RawStartSamples = GenerateStartSamples();
    private static readonly short[] RawSuccessSamples = GenerateSuccessSamples();
    private static readonly short[] RawErrorSamples = GenerateErrorSamples();

    private readonly object _playerLock = new();
    private double _cachedVolume = -1.0;
    private SoundPlayer? _startPlayer;
    private SoundPlayer? _successPlayer;
    private SoundPlayer? _errorPlayer;

    public SoundEffectService(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
    }

    public void PlayRecordingStart()
    {
        PlayTone(SoundType.Start);
    }

    public void PlayDictationSuccess()
    {
        PlayTone(SoundType.Success);
    }

    public void PlayError()
    {
        PlayTone(SoundType.Error);
    }

    private enum SoundType { Start, Success, Error }

    private void PlayTone(SoundType type)
    {
        try
        {
            var settings = _settingsManager.Load();
            if (!settings.PlayAlertSounds || settings.SoundVolume <= 0)
            {
                return;
            }

            var volume = settings.SoundVolume;
            SoundPlayer? player;

            lock (_playerLock)
            {
                if (Math.Abs(_cachedVolume - volume) > 0.001 || _startPlayer == null)
                {
                    RebuildPlayers(volume);
                }

                player = type switch
                {
                    SoundType.Start => _startPlayer,
                    SoundType.Success => _successPlayer,
                    SoundType.Error => _errorPlayer,
                    _ => _startPlayer
                };
            }

            player?.Play();
        }
        catch
        {
            // Audio feedback is best-effort and must never crash app
        }
    }

    private void RebuildPlayers(double volume)
    {
        var volumeScale = Math.Clamp(volume / 100.0, 0.0, 1.0) * 0.35; // Cap at 35% max amplitude for pleasant subtlety
        _startPlayer = CreatePlayer(RawStartSamples, volumeScale);
        _successPlayer = CreatePlayer(RawSuccessSamples, volumeScale);
        _errorPlayer = CreatePlayer(RawErrorSamples, volumeScale);
        _cachedVolume = volume;
    }

    private static SoundPlayer CreatePlayer(short[] baseSamples, double volumeScale)
    {
        var scaled = new short[baseSamples.Length];
        for (int i = 0; i < baseSamples.Length; i++)
        {
            scaled[i] = (short)(baseSamples[i] * volumeScale);
        }

        var wavBytes = CreateWavBytes(scaled, SampleRate);
        var ms = new MemoryStream(wavBytes);
        var player = new SoundPlayer(ms);
        try
        {
            player.Load();
        }
        catch
        {
            // Best effort preload
        }
        return player;
    }

    private static short[] GenerateStartSamples()
    {
        // Rising subtle chime: 480 Hz -> 660 Hz over 90ms
        int sampleCount = (int)(SampleRate * (90 / 1000.0));
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            var t = (double)i / sampleCount;
            var freq = 480.0 + (180.0 * t);
            var phase = 2.0 * Math.PI * freq * (i / (double)SampleRate);
            var env = Envelope(i, sampleCount, attackSamples: 400, decaySamples: 800);
            samples[i] = (short)(Math.Sin(phase) * env * 32767.0);
        }
        return samples;
    }

    private static short[] GenerateSuccessSamples()
    {
        // Two-tone pleasant confirmation chime: 784 Hz (G5) -> 1046 Hz (C6) over 130ms
        int sampleCount = (int)(SampleRate * (130 / 1000.0));
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
    }

    private static short[] GenerateErrorSamples()
    {
        // Descending low cue: 320 Hz -> 220 Hz over 130ms
        int sampleCount = (int)(SampleRate * (130 / 1000.0));
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
