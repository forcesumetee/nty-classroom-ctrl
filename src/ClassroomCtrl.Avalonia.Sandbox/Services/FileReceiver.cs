using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>Outcome of one completed (or failed) file transfer.</summary>
public sealed record FileReceiveResult(string FileName, string? SavedPath, bool Ok, long SizeBytes, string? Error);

/// <summary>
/// TT-12 — the Student side of "teacher sends a file to the class". The wire pipeline
/// (FileAnnounce 0x0200 / FileChunk 0x0201 / FileComplete 0x0203, all on the RELIABLE channel) was
/// fully present in Shared.Wire and the Teacher already BROADCASTS it (ControlServer.BroadcastFileAsync),
/// but the macOS Student had ZERO subscribers — exactly the TT-9 "decoded and dropped" pattern that
/// hid student-audio mixing. This reassembles chunks by TransferId, VERIFIES the SHA-256 the teacher
/// announced (a truncated/corrupt transfer is reported, never silently saved), and writes the file.
///
/// Pure: no Avalonia, no UI thread — System.IO + MessagePack + Shared.Wire only. The ConnectionViewModel
/// dispatch feeds it (after the StudentEnvelopeFilter.IsForMe gate, so a targeted send only lands on its
/// target), and the headless gate (MockStudent --filetest) drives the SAME code end-to-end.
///
/// Targeting is NOT this class's concern: a file targeted at student A is dropped for B by IsForMe
/// BEFORE it ever reaches B's FileReceiver (the distinguishing negative rides the same filter as every
/// per-student command — TT-6-D).
/// </summary>
public sealed class FileReceiver
{
    private sealed class Transfer
    {
        public required FileAnnounceMessage Announce;
        public required byte[]?[] Chunks;
        public int Received;
    }

    private readonly string _saveDir;
    private readonly Dictionary<Guid, Transfer> _transfers = new();
    private readonly object _gate = new();

    /// <summary>Raised on the calling (transport) thread when a transfer completes or fails.</summary>
    public event Action<FileReceiveResult>? FileReceived;

    /// <param name="saveDir">Where received files land. Defaults to ~/Downloads/NTY ClassroomCtrl.</param>
    public FileReceiver(string? saveDir = null)
    {
        _saveDir = saveDir ?? DefaultSaveDir();
        Directory.CreateDirectory(_saveDir);
    }

    public string SaveDirectory => _saveDir;

    public static string DefaultSaveDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var baseDir = Directory.Exists(downloads) ? downloads : home;
        return Path.Combine(baseDir, "NTY ClassroomCtrl");
    }

    public void OnAnnounce(FileAnnounceMessage a)
    {
        if (a.ChunkCount < 0) return;
        lock (_gate)
            _transfers[a.TransferId] = new Transfer
            {
                Announce = a,
                Chunks = new byte[a.ChunkCount][],
                Received = 0,
            };
    }

    public void OnChunk(FileChunkMessage c)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(c.TransferId, out var t)) return;
            if (c.ChunkIndex < 0 || c.ChunkIndex >= t.Chunks.Length) return;
            if (t.Chunks[c.ChunkIndex] != null) return;   // ignore a duplicate (reliable channel shouldn't, but be safe)
            t.Chunks[c.ChunkIndex] = c.Data;
            t.Received++;
        }
    }

    /// <summary>Reassemble, verify the announced SHA-256, and (only if it matches) write the file.</summary>
    public FileReceiveResult OnComplete(FileCompleteMessage comp)
    {
        Transfer? t;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(comp.TransferId, out t)) t = null;
            else _transfers.Remove(comp.TransferId);
        }
        if (t == null)
            return Report(new FileReceiveResult(comp.FileName, null, false, 0, "no matching announce (dropped/never targeted)"));

        long size = 0;
        for (int i = 0; i < t.Chunks.Length; i++)
        {
            if (t.Chunks[i] == null)
                return Report(new FileReceiveResult(t.Announce.FileName, null, false, 0, $"missing chunk {i}/{t.Chunks.Length} (truncated)"));
            size += t.Chunks[i]!.Length;
        }

        var bytes = new byte[size];
        long off = 0;
        foreach (var ch in t.Chunks) { Buffer.BlockCopy(ch!, 0, bytes, (int)off, ch!.Length); off += ch!.Length; }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        bool ok = string.Equals(sha, t.Announce.Sha256Hex, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            return Report(new FileReceiveResult(t.Announce.FileName, null, false, size, "SHA-256 mismatch (corrupt transfer)"));

        // Strip any directory components from the announced name (no path traversal) + de-dup.
        var safeName = Path.GetFileName(t.Announce.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"received-{comp.TransferId:N}";
        var path = UniquePath(_saveDir, safeName);
        File.WriteAllBytes(path, bytes);
        return Report(new FileReceiveResult(t.Announce.FileName, path, true, size, null));
    }

    private FileReceiveResult Report(FileReceiveResult r) { FileReceived?.Invoke(r); return r; }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
