using System;
using System.IO;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Shared.Attachments;

/// <summary>
/// Phase 19 (v1.1) — persists chat-embedded file attachments to the local
/// filesystem + surfaces Download / Open helpers for the chat-bubble UI.
///
/// Storage layout (LocalApplicationData\NTY\ClassroomCtrl\Attachments\):
///   {AttachmentId}_{OriginalFileName}
///
/// Id-prefix scoping: different chats can attach files with the same name
/// without collision; cleanup-by-Id is straightforward when v1.2 adds a
/// retention sweep.
///
/// Design choices:
///   - Path.Combine guards against ".." / absolute paths in the original
///     filename — we strip directory components before composing the local
///     name.  The MessagePack [Key(1)] field is sender-controlled but the
///     Classroom-Mode threat model trusts the LAN; this is belt-and-braces.
///   - Process.Start with UseShellExecute=true is the only safe way to
///     "open with default app" on Windows desktop apps in .NET 6+; UseShell
///     defaults to false post-Core, so it must be explicit.
///   - No async open — Process.Start is fire-and-forget by design.
///   - SaveAsync uses File.WriteAllBytesAsync so large attachments don't
///     block the dispatch arm.
/// </summary>
public class AttachmentManager
{
    private readonly string _basePath;

    public AttachmentManager(string? overrideBasePath = null)
    {
        _basePath = overrideBasePath
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "NTY", "ClassroomCtrl", "Attachments");
    }

    /// <summary>Local filesystem path for the given attachment.  Returned
    /// path is deterministic from id + filename so a re-arriving chat
    /// envelope (re-broadcast from a teacher restart) overwrites cleanly
    /// without orphans.</summary>
    public string GetAttachmentPath(Guid attachmentId, string fileName)
    {
        // Strip directory components from the sender-supplied filename — the
        // Classroom-Mode threat model trusts the LAN but a future tier with
        // multi-school relay could see a tampered name.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "attachment.bin";
        return Path.Combine(_basePath, $"{attachmentId}_{safeName}");
    }

    /// <summary>Persist a received attachment to local storage.  Idempotent:
    /// re-arriving same-id chats overwrite the file at the same path.
    /// Best-effort: any IO failure is propagated so the chat dispatch arm
    /// can mark the attachment "failed" in the UI.</summary>
    public async Task<string> SaveAsync(FileAttachment attachment)
    {
        Directory.CreateDirectory(_basePath);
        var path = GetAttachmentPath(attachment.Id, attachment.FileName);
        await File.WriteAllBytesAsync(path, attachment.Data).ConfigureAwait(false);
        return path;
    }

    /// <summary>Does the local copy exist?  Used by the chat-bubble UI to
    /// gate the Open button visibility — sometimes a chat envelope arrives
    /// after the agent process restart that wiped its in-memory cache;
    /// if the file is still on disk we can still let the user open it.</summary>
    public bool Exists(Guid attachmentId, string fileName)
        => File.Exists(GetAttachmentPath(attachmentId, fileName));

    /// <summary>Open the local copy with the OS default app
    /// (UseShellExecute=true).  Caller responsible for ensuring Exists()
    /// first or wrapping in try/catch — Process.Start throws if the path
    /// doesn't resolve.</summary>
    public void Open(Guid attachmentId, string fileName)
    {
        var path = GetAttachmentPath(attachmentId, fileName);
        if (!File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    /// <summary>Copy the local cache to a user-selected save location.
    /// Caller wires the SaveFileDialog; this helper just performs the
    /// copy.  Overwrites the destination if it exists.</summary>
    public void CopyTo(Guid attachmentId, string fileName, string destinationPath)
    {
        var source = GetAttachmentPath(attachmentId, fileName);
        if (!File.Exists(source)) return;
        File.Copy(source, destinationPath, overwrite: true);
    }

    /// <summary>Format a file size in bytes as a human-readable string
    /// (e.g. "1.2 MB", "250 KB").  Used in chat-bubble metadata strip;
    /// kept in the manager so both Teacher + Student render identically.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double k = bytes / 1024.0;
        if (k < 1024) return $"{k:0.#} KB";
        double m = k / 1024.0;
        if (m < 1024) return $"{m:0.#} MB";
        return $"{m / 1024:0.#} GB";
    }
}
