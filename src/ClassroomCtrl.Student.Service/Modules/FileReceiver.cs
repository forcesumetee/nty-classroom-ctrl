using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Reassembles file chunks (Spec §6.4) and saves to per-user Documents\Classroom\.
/// 
/// Design:
///   - Buffer chunks in memory until FileComplete arrives
///   - On complete: write all chunks to disk in order
///   - Path: %PUBLIC%\Documents\Classroom\{filename} (LocalSystem-friendly)
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
            // Save under Public Documents (works for LocalSystem and any user)
            var publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            var classroomDir = Path.Combine(publicDocs, "Classroom");
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