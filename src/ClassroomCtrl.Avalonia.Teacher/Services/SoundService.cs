using System;
using System.Diagnostics;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-7-C — macOS notification sounds via <c>afplay</c> on the built-in system sounds.
/// Deliberately NOT new native: batch 1 reuses only existing native, and <c>afplay</c>
/// is a stock macOS CLI, so this needs no dylib change. Fire-and-forget; every failure
/// is swallowed — a missing sound file or a spawn error must never disrupt the class.
///
/// Sandbox caveat (P35): the Teacher app is unsandboxed today, so <c>Process.Start</c> is
/// fine. If a future App-Sandbox entitlement is added, switch this to NSSound / a native
/// clip API — the <see cref="ISoundService"/> seam localizes that change to this file.
/// </summary>
public sealed class SoundService : ISoundService
{
    // Stock macOS system sounds. Ping = attention (hand-raise); Pop = soft (chat).
    private const string HandRaisePath = "/System/Library/Sounds/Ping.aiff";
    private const string ChatPath = "/System/Library/Sounds/Pop.aiff";

    public void Play(NotificationSound sound)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = sound switch
        {
            NotificationSound.HandRaise => HandRaisePath,
            NotificationSound.Chat => ChatPath,
            _ => ChatPath,
        };
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/afplay", $"\"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            using var p = Process.Start(psi);
            // Fire-and-forget: afplay exits on its own once the clip finishes.
        }
        catch
        {
            // A notification sound is never worth throwing over.
        }
    }
}
