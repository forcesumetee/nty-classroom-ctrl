using System;
using System.IO;

namespace ClassroomCtrl.Shared;

/// <summary>
/// Phase 10.21 — single source of truth for cross-process file paths.
///
/// Phase 10.20's investigation revealed the original Net Movie bug: the
/// receiver (Service) wrote files to one path and the player (Agent) looked
/// for them in another, because Phase 10.13 moved the destination but only
/// updated one side.  Two files duplicating the same path string is exactly
/// the tech-debt that produced that regression.  Centralising the path here
/// makes a future drift impossible: both sides reference one constant.
///
/// Phase 10.21 also moves the destination off the user Desktop.  On
/// M365-managed school PCs (and on the dev box) the Desktop is typically
/// redirected into OneDrive, so writing a teacher's 30 MB video there
/// uploads it to the student's cloud, clutters the Desktop, and risks
/// future hydration stalls.  %LOCALAPPDATA%\NTY\ClassroomCtrl\NetMovie\
/// is local, fast, private, and never synced.
/// </summary>
public static class StoragePaths
{
    /// <summary>
    /// Directory where the FileReceiver writes a Net Movie payload and where
    /// the Agent's MoviePlayerWindow reads it back.  Created on demand by
    /// <see cref="EnsureNetMovieDir"/>.
    /// </summary>
    public static string NetMovieDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NTY", "ClassroomCtrl", "NetMovie");

    /// <summary>
    /// Resolve the on-disk path the player should open for the given file name.
    /// File names arriving from the network are passed through
    /// <see cref="Path.GetFileName(string)"/> to strip any path component a
    /// malformed peer could embed.
    /// </summary>
    public static string GetNetMovieFilePath(string fileName)
        => Path.Combine(NetMovieDir, Path.GetFileName(fileName));

    /// <summary>
    /// Create the NetMovie directory if missing.  Returns the directory path.
    /// </summary>
    public static string EnsureNetMovieDir()
    {
        var dir = NetMovieDir;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
