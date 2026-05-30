using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-D step 1 — Meet-style bottom toolbar slotted into ConferenceView.
/// Commands route through the host view-model (the parent DataContext).
/// The ⋮ More button opens a Popup declared as a Button resource.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
///
/// Phase 16-B+ step 2 — UI bug fix.  The Popup is now a sibling in the
/// visual tree (wrapped with the More button in a Grid), so the
/// MorePopup field is generated normally from x:Name.  The reaction
/// buttons use Click handlers + Tag instead of Command binding so the
/// path is robust even on shells (Student) whose VM may not expose
/// SendReactionCommand yet — we resolve it dynamically against the
/// current DataContext.
/// </summary>
public partial class ConferenceToolbar : UserControl
{
    public ConferenceToolbar()
    {
        InitializeComponent();
    }

    private static bool _emojiDiagLogged;

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = !MorePopup.IsOpen;

        // Phase 16-X (Bug D Part 1 retry, 2026-05-31) — Strategy 5
        // diagnostic.  On the first Popup open per process, log whether
        // "Segoe UI Emoji" actually resolves to a real font on this PC + if
        // its GlyphTypeface has the codepoints we ask for.  Catches the
        // case where the font name doesn't exist (Win11 locale variants
        // without the emoji font), where the font resolves but lacks a
        // specific glyph, or where the font shaper drops surrogate pairs.
        // One-shot via _emojiDiagLogged so repeated picker opens don't
        // spam the log file.
        if (MorePopup.IsOpen && !_emojiDiagLogged)
        {
            _emojiDiagLogged = true;
            try { LogEmojiDiagnostic(); }
            catch { /* diagnostic must never crash the picker */ }
        }
    }

    private void LogEmojiDiagnostic()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[ConferenceToolbar Bug D diagnostic] {DateTime.Now:O}");
        int[] probeCodepoints = { 0x1F44D, 0x2764, 0x1F602, 0x1F62E, 0x1F622 };
        string[] candidates = { "Segoe UI Emoji", "Segoe UI Symbol", "Segoe UI" };
        foreach (var name in candidates)
        {
            try
            {
                var ff = new FontFamily(name);
                var typefaces = ff.GetTypefaces();
                var primary = typefaces.FirstOrDefault();
                sb.AppendLine($"  Font candidate '{name}': resolved typefaces={typefaces.Count}");
                if (primary != null && primary.TryGetGlyphTypeface(out var gt))
                {
                    var map = gt.CharacterToGlyphMap;
                    foreach (var cp in probeCodepoints)
                    {
                        bool has = map.ContainsKey(cp);
                        sb.AppendLine($"    U+{cp:X4} → has glyph: {has}");
                    }
                }
                else
                {
                    sb.AppendLine("    (no GlyphTypeface resolvable)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Font candidate '{name}': probe threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        var path = Path.Combine(temp, "conference-toolbar-fontdiag.log");
        File.AppendAllText(path, sb.ToString());
    }

    private void ReactionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string emoji) return;
        MorePopup.IsOpen = false;

        // Resolve SendReactionCommand against the toolbar's DataContext
        // (the host shell VM — MainViewModel on Teacher,
        // StudentConferenceShellViewModel on Student).  Reflection avoids
        // taking a hard dep on a shared interface — Phase 16-D introduces
        // IConferenceShellViewModel and we can swap to a direct cast then.
        if (DataContext == null) return;
        var prop = DataContext.GetType().GetProperty("SendReactionCommand");
        if (prop?.GetValue(DataContext) is not ICommand cmd) return;
        if (cmd.CanExecute(emoji)) cmd.Execute(emoji);
    }
}
