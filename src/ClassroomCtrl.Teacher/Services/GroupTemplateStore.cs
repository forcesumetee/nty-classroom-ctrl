using System.IO;
using System.Text.Json;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 13-B (Tier 1) — JSON-persisted breakout-group TEMPLATES (room names +
/// intended membership by student display-name).  Active membership state is
/// session-only; templates survive teacher restart so weekly project teams can
/// be reapplied without recreating the structure.
///
/// Path: <c>%ProgramData%\NTY\ClassroomCtrl\groups.json</c>.  ProgramData chosen
/// so the file is machine-wide (matches the existing convention used by
/// teacher-debug.log paths in Phase 10.21).
///
/// Members are stored by DISPLAY-NAME, not Guid: Guids regenerate per session
/// and would not match across teacher restarts, while display names persist.
/// On load, the caller matches names against currently-online students and
/// silently drops unmatched names.
/// </summary>
public class GroupTemplateStore
{
    private static readonly string StorePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl", "groups.json");

    public class TemplateGroup
    {
        public string Name { get; set; } = "";
        public List<string> MemberDisplayNames { get; set; } = new();
    }

    public class Template
    {
        public string Name { get; set; } = "";
        public List<TemplateGroup> Groups { get; set; } = new();
    }

    public class Store
    {
        public List<Template> Templates { get; set; } = new();
    }

    public Store Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Store();
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<Store>(json) ?? new Store();
        }
        catch
        {
            // Corrupted file → treat as empty; don't crash the UI.
            return new Store();
        }
    }

    public void Save(Store store)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StorePath, json);
    }

    /// <summary>Overwrite-by-name semantics; callers responsible for the
    /// "overwrite existing?" confirmation prompt (per UX decisions).</summary>
    public void UpsertTemplate(Template template)
    {
        var store = Load();
        var existing = store.Templates.FirstOrDefault(t =>
            string.Equals(t.Name, template.Name, System.StringComparison.OrdinalIgnoreCase));
        if (existing != null) store.Templates.Remove(existing);
        store.Templates.Add(template);
        Save(store);
    }

    public bool TemplateExists(string name)
    {
        return Load().Templates.Any(t =>
            string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase));
    }
}
