using ClassroomCtrl.Shared.Models.Roster;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 9.3: Manages the list of saved class rosters on disk and tracks the
/// "active" roster (used by the main classroom view to mark attendance and
/// display class name in the header).
/// </summary>
public class ClassRosterService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl", "Rosters");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public ObservableCollection<ClassRoster> Rosters { get; } = new();
    public ClassRoster? ActiveRoster { get; private set; }

    public event EventHandler<ClassRoster?>? ActiveRosterChanged;

    public ClassRosterService()
    {
        try { Directory.CreateDirectory(Folder); }
        catch { /* best-effort */ }
        LoadRosters();
    }

    public void LoadRosters()
    {
        Rosters.Clear();
        if (!Directory.Exists(Folder)) return;

        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var roster = JsonSerializer.Deserialize<ClassRoster>(json, JsonOpts);
                if (roster != null)
                    Rosters.Add(roster);
            }
            catch { /* skip corrupt files */ }
        }
    }

    public void SaveRoster(ClassRoster roster)
    {
        if (roster.Id == Guid.Empty) roster.Id = Guid.NewGuid();
        try { Directory.CreateDirectory(Folder); } catch { }

        var path = Path.Combine(Folder, $"{roster.Id:N}.json");
        var json = JsonSerializer.Serialize(roster, JsonOpts);
        File.WriteAllText(path, json);

        var existing = Rosters.FirstOrDefault(r => r.Id == roster.Id);
        if (existing != null)
        {
            var idx = Rosters.IndexOf(existing);
            Rosters[idx] = roster;
        }
        else
        {
            Rosters.Add(roster);
        }
    }

    public void DeleteRoster(Guid id)
    {
        var path = Path.Combine(Folder, $"{id:N}.json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var existing = Rosters.FirstOrDefault(r => r.Id == id);
        if (existing != null) Rosters.Remove(existing);
        if (ActiveRoster?.Id == id) SetActive(null);
    }

    public void SetActive(Guid? id)
    {
        if (id == null)
        {
            ActiveRoster = null;
        }
        else
        {
            ActiveRoster = Rosters.FirstOrDefault(r => r.Id == id.Value);
            if (ActiveRoster != null)
            {
                ActiveRoster.LastUsedAt = DateTime.UtcNow;
                SaveRoster(ActiveRoster);
            }
        }
        ActiveRosterChanged?.Invoke(this, ActiveRoster);
    }

    /// <summary>
    /// For each enrolled student, set IsPresent = true if a connected student matches by
    /// MachineName (case-insensitive). Returns the count of present students.
    /// </summary>
    public int MarkAttendance(IEnumerable<string> connectedMachineNames)
    {
        if (ActiveRoster == null) return 0;
        var connected = new HashSet<string>(connectedMachineNames, StringComparer.OrdinalIgnoreCase);
        int present = 0;
        foreach (var s in ActiveRoster.Students)
        {
            s.IsPresent = !string.IsNullOrEmpty(s.MachineName) && connected.Contains(s.MachineName);
            if (s.IsPresent) present++;
        }
        return present;
    }

    public string ExportToJson(ClassRoster roster) => JsonSerializer.Serialize(roster, JsonOpts);

    public ClassRoster? ImportFromJson(string json)
    {
        try
        {
            var roster = JsonSerializer.Deserialize<ClassRoster>(json, JsonOpts);
            if (roster == null) return null;
            roster.Id = Guid.NewGuid();   // avoid collisions with existing
            SaveRoster(roster);
            return roster;
        }
        catch { return null; }
    }
}
