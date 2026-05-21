using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Reassembles file chunks (Spec §6.4) and saves to user's Desktop\ClassroomFiles\.
/// Phase 10.13 changed from %PUBLIC%\Documents\Classroom because Phase 10.9 moved
/// Service from LocalSystem (Session 0) to user session — can write user Desktop directly.
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

        try
        {
            // Phase 10.13 — save to user's Desktop for visibility (Service now runs in user
            // session per Phase 10.9; was %PUBLIC%\Documents\Classroom\ when Service was LocalSystem).
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var classroomDir = Path.Combine(userDesktop, "ClassroomFiles");
            Directory.CreateDirectory(classroomDir);

            var safeName = Path.GetFileName(t.FileName); // strip any path
            var outPath = Path.Combine(classroomDir, safeName);

            using var fs = File.Create(outPath);
            for (int i = 0; i < t.ExpectedChunks; i++)
            {
                if (t.Chunks.TryGetValue(i, out var data))
                {
                    fs.Write(data, 0, data.Length);
                }
                else
                {
                    _logger.LogWarning("Missing chunk {Idx} for {File}", i, t.FileName);
                }
            }

            _logger.LogInformation("File saved: {Path}", outPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save received file: {Name}", t.FileName);
        }
    }
}