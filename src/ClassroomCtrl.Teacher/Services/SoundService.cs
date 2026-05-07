using Microsoft.Win32;
using System;
using System.IO;
using System.Media;

namespace ClassroomCtrl.Teacher.Services;

public enum SoundEvent
{
    ChatDing,
    HandRaise,
    StudentConnect,
    MicOn,
    MicOff,
    Notification,
}

/// <summary>
/// Phase 3.5: Audio cues for teacher UI events. Uses generated PCM sine waves
/// (no external .wav assets needed). Persists ON/OFF in HKCU.
/// </summary>
public static class SoundService
{
    private static byte[]? _chatDingWav;
    private static byte[]? _handRaiseWav;
    private static byte[]? _studentConnectWav;
    private static byte[]? _micOnWav;
    private static byte[]? _micOffWav;
    private static byte[]? _notificationWav;

    public static bool IsEnabled { get; set; } = true;

    public static void Initialize()
    {
        IsEnabled = ReadEnabledFromRegistry();
        _chatDingWav = GenerateSineWav(800, 100, 0.4);
        _handRaiseWav = GenerateTwoToneWav(600, 800, 200, 0.4);
        _studentConnectWav = GenerateTwoToneWav(500, 700, 80, 0.4);
        _micOnWav = GenerateSineWav(600, 50, 0.4);
        _micOffWav = GenerateSineWav(400, 50, 0.4);
        _notificationWav = GenerateSineWav(1000, 60, 0.4);
    }

    public static void Play(SoundEvent ev)
    {
        if (!IsEnabled) return;
        var wav = ev switch
        {
            SoundEvent.ChatDing => _chatDingWav,
            SoundEvent.HandRaise => _handRaiseWav,
            SoundEvent.StudentConnect => _studentConnectWav,
            SoundEvent.MicOn => _micOnWav,
            SoundEvent.MicOff => _micOffWav,
            SoundEvent.Notification => _notificationWav,
            _ => null,
        };
        if (wav == null) return;

        try
        {
            using var ms = new MemoryStream(wav);
            using var player = new SoundPlayer(ms);
            player.Play();   // async; SoundPlayer dispatches to a background thread
        }
        catch { /* swallow — sound is optional */ }
    }

    public static void SetEnabled(bool on)
    {
        IsEnabled = on;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NTY\ClassroomCtrl");
            key?.SetValue("SoundsEnabled", on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static bool ReadEnabledFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\NTY\ClassroomCtrl");
            var v = key?.GetValue("SoundsEnabled");
            if (v is int i) return i != 0;
        }
        catch { }
        return true;
    }

    /// <summary>Builds a PCM 16-bit mono 44.1 kHz WAV file as a byte array.</summary>
    private static byte[] GenerateSineWav(double freqHz, int durationMs, double volume)
    {
        const int sampleRate = 44100;
        int totalSamples = sampleRate * durationMs / 1000;
        return BuildWav(totalSamples, i =>
        {
            double t = (double)i / sampleRate;
            // Apply 5ms attack/release envelope to avoid clicks
            double env = AttackRelease(i, totalSamples, sampleRate);
            return (short)(Math.Sin(2 * Math.PI * freqHz * t) * volume * env * 32767);
        }, sampleRate);
    }

    private static byte[] GenerateTwoToneWav(double f1, double f2, int durationMs, double volume)
    {
        const int sampleRate = 44100;
        int totalSamples = sampleRate * durationMs / 1000;
        int half = totalSamples / 2;
        return BuildWav(totalSamples, i =>
        {
            double freq = i < half ? f1 : f2;
            int local = i < half ? i : i - half;
            int localTotal = i < half ? half : (totalSamples - half);
            double t = (double)i / sampleRate;
            double env = AttackRelease(local, localTotal, sampleRate);
            return (short)(Math.Sin(2 * Math.PI * freq * t) * volume * env * 32767);
        }, sampleRate);
    }

    private static double AttackRelease(int i, int totalSamples, int sampleRate)
    {
        int rampSamples = sampleRate / 200; // 5 ms
        if (rampSamples <= 0 || totalSamples <= rampSamples * 2) return 1.0;
        if (i < rampSamples) return (double)i / rampSamples;
        if (i > totalSamples - rampSamples) return (double)(totalSamples - i) / rampSamples;
        return 1.0;
    }

    private static byte[] BuildWav(int totalSamples, Func<int, short> samplerFn, int sampleRate)
    {
        int byteRate = sampleRate * 2;             // 16-bit mono
        int subchunk2Size = totalSamples * 2;
        int chunkSize = 36 + subchunk2Size;

        using var ms = new MemoryStream(44 + subchunk2Size);
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                  // subchunk1 size
        bw.Write((short)1);            // PCM
        bw.Write((short)1);            // mono
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)2);            // block align
        bw.Write((short)16);           // bits per sample

        // data subchunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(subchunk2Size);
        for (int i = 0; i < totalSamples; i++)
            bw.Write(samplerFn(i));

        bw.Flush();
        return ms.ToArray();
    }
}
