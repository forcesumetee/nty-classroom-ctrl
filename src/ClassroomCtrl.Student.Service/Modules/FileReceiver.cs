using ClassroomCtrl.Shared;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Reassembles file chunks (Spec §6.4) and saves to <see cref="StoragePaths.NetMovieDir"/>.
///
/// Phase 10.21 — three changes from Phase 10.13's version:
///   • Atomic publish: write to <c>&lt;name&gt;.part</c>, then <see cref="File.Move(string,string,bool)"/>
///     to the final name once every chunk is written.  Prevents any reader
///     (Agent's MoviePlayerWindow) from ever seeing a partial file.
///   • Loud failures: any missing chunk now logs as Error (was Warning) AND
///     the published file is suppressed — better to fail visibly than ship
///     a truncated video that decodes into glitches.  Pre-10.21 the receiver
///     silently wrote whatever chunks it had and the player happily decoded
///     it until the truncation boundary.
///   • Shared destination: path comes from <see cref="StoragePaths.GetNetMovieFilePath"/>
///     so the Agent's player can never again drift from where this writer
///     places the file.  That drift was the original Phase 10.20 regression.
/// </summary>
public class FileReceiver
{
    private class ActiveTransfer
    {
        public string FileName = "";
        public long SizeBytes;
        public int ExpectedChunks;
        public string Sha256Hex = "";
        public ConcurrentDictionary<int, byte[]> Chunks = new();
    }

    private readonly ILogger<FileReceiver> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveTransfer> _transfers = new();

    public FileReceiver(ILogger<FileReceiver> logger) => _logger = logger;

    public void Announce(FileAnnounceMessage m)
    {
        _transfers[m.TransferId] = new ActiveTransfer
        {
            FileName = m.FileName,
            SizeBytes = m.SizeBytes,
            ExpectedChunks = m.ChunkCount,
            Sha256Hex = m.Sha256Hex,
        };
        _logger.LogInformation("File transfer starting: {Name} ({Size} bytes, {Chunks} chunks)",
            m.FileName, m.SizeBytes, m.ChunkCount);
    }

    public void Chunk(FileChunkMessage m)
    {
        if (!_transfers.TryGetValue(m.TransferId, out var t))
        {
            _logger.LogWarning("Chunk for unknown transfer {Id}", m.TransferId);
            return;
        }
        t.Chunks[m.ChunkIndex] = m.Data;
    }

    public void Complete(FileCompleteMessage m)
    {
        if (!_transfers.TryRemove(m.TransferId, out var t))
        {
            _logger.LogWarning("Complete for unknown transfer {Id}", m.TransferId);
            return;
        }

        var classroomDir = StoragePaths.EnsureNetMovieDir();
        var finalPath = StoragePaths.GetNetMovieFilePath(t.FileName);
        // Same directory as finalPath so File.Move is atomic (same volume).
        var partPath = finalPath + ".part";

        // Fail-loud chunk check.  Pre-10.21 we silently skipped missing chunks
        // and wrote a corrupted/short file — the player then decoded artifacts
        // until the truncation boundary.  Post-10.21 the reliable broadcast
        // path should make this impossible; if it ever fires again the file
        // is suppressed instead of published in a broken state.
        var missing = new List<int>();
        for (int i = 0; i < t.ExpectedChunks; i++)
        {
            if (!t.Chunks.ContainsKey(i)) missing.Add(i);
        }
        if (missing.Count > 0)
        {
            _logger.LogError(
                "Refusing to publish {Name}: {Missing}/{Total} chunks missing (first missing: {First}). " +
                "Partial file not exposed; transfer aborted.",
                t.FileName, missing.Count, t.ExpectedChunks, missing[0]);
            TryDelete(partPath);
            return;
        }

        long written = 0;
        try
        {
            using (var fs = File.Create(partPath))
            {
                for (int i = 0; i < t.ExpectedChunks; i++)
                {
                    var data = t.Chunks[i];
                    fs.Write(data, 0, data.Length);
                    written += data.Length;
                }
                fs.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            // Loud, not silent.  Spec calls this out — pre-10.21 the catch was
            // _logger.LogError but the partial .part file was left in place,
            // which on retry-with-same-name created a confusing layered state.
            _logger.LogError(ex,
                "Failed writing {Name}: wrote {Written}/{Expected} bytes before {ExType}: {Msg}. " +
                "Discarding .part file.",
                t.FileName, written, t.SizeBytes, ex.GetType().Name, ex.Message);
            TryDelete(partPath);
            return;
        }

        if (written != t.SizeBytes)
        {
            _logger.LogError(
                "Size mismatch publishing {Name}: wrote {Written} bytes, announced {Expected}. " +
                "Discarding .part file.",
                t.FileName, written, t.SizeBytes);
            TryDelete(partPath);
            return;
        }

        try
        {
            // Atomic publish: any earlier file with the same name (e.g. a stale
            // copy from a previous Net Movie session) is replaced as one step;
            // the Agent's poll-for-existence loop can never observe a half-written
            // file under the final name.
            File.Move(partPath, finalPath, overwrite: true);
            _logger.LogInformation("File saved (atomic): {Path} ({Size} bytes)", finalPath, written);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Atomic rename failed for {Name}: {ExType}: {Msg}",
                t.FileName, ex.GetType().Name, ex.Message);
            TryDelete(partPath);
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", path); }
    }
}
