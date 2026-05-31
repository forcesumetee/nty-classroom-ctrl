using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ClassroomCtrl.Shared.Models;
using ClassroomCtrl.Shared.Wpf.ViewModels;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-D step 2 — slide-in sidebar overlay for ConferenceView.  Hosts
/// Chat (step 2) and Participants (step 3) tabs.  Lives as an overlay on the
/// right side of the gallery so toggling the sidebar doesn't reflow the
/// tiles.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
/// Close X uses IConferenceSidebarHost instead of a direct MainViewModel
/// reference so the same control works on Student.Agent's shell.
/// </summary>
public partial class ConferenceSidebar : UserControl
{
    public ConferenceSidebar()
    {
        InitializeComponent();
    }

    /// <summary>Close X — pulls IsConferenceSidebarVisible to false via
    /// IConferenceSidebarHost.  Avoids cycling through the toggle command
    /// (which has special "open to Chat" semantics that would surprise the
    /// user when closing from the Participants tab).</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is IConferenceSidebarHost host)
        {
            host.IsConferenceSidebarVisible = false;
        }
    }

    /// <summary>Phase 19 (v1.1) — 📎 attach-file button.  Bridges to the
    /// host shell VM's <c>OpenAttachmentPicker()</c> method via reflection so
    /// Shared.Wpf doesn't need a hard reference to either Teacher.MainViewModel
    /// or StudentConferenceShellViewModel (same name on both surfaces).
    /// Same indirection style as ReactionButton_Click for SendReactionCommand.</summary>
    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext == null) return;
        var method = DataContext.GetType().GetMethod("OpenConferenceAttachmentPicker",
                         BindingFlags.Public | BindingFlags.Instance)
                  ?? DataContext.GetType().GetMethod("OpenAttachmentPicker",
                         BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(DataContext, null);
    }

    /// <summary>Phase 19 (v1.1) — ✕ on the draft attachment preview strip.</summary>
    private void ClearDraftAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext == null) return;
        var method = DataContext.GetType().GetMethod("ClearConferenceDraftAttachment",
                         BindingFlags.Public | BindingFlags.Instance)
                  ?? DataContext.GetType().GetMethod("ClearDraftAttachment",
                         BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(DataContext, null);
    }

    /// <summary>Phase 19 (v1.1) — Open the local copy of a chat bubble's
    /// attachment via the OS default app.  AttachmentManager.Open is
    /// available statically through the shell VM's App.Attachments
    /// singleton; the bubble template raises this click with the chat
    /// message VM as its Tag so we can extract the FileAttachment.</summary>
    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ChatMessage cm) return;
        if (cm.Attachment == null) return;
        TryInvokeOnApp("Attachments", manager =>
        {
            var open = manager.GetType().GetMethod("Open", new[] { typeof(System.Guid), typeof(string) });
            open?.Invoke(manager, new object?[] { cm.Attachment.Id, cm.Attachment.FileName });
        });
    }

    /// <summary>Phase 19 (v1.1) — Save the cached attachment to a user-
    /// picked location via SaveFileDialog → AttachmentManager.CopyTo.</summary>
    private void DownloadAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ChatMessage cm) return;
        if (cm.Attachment == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = cm.Attachment.FileName,
        };
        if (dlg.ShowDialog() != true) return;

        TryInvokeOnApp("Attachments", manager =>
        {
            var copy = manager.GetType().GetMethod("CopyTo",
                new[] { typeof(System.Guid), typeof(string), typeof(string) });
            copy?.Invoke(manager, new object?[] { cm.Attachment.Id, cm.Attachment.FileName, dlg.FileName });
        });
    }

    /// <summary>Reflection bridge: look up Application.Current.GetType().
    /// GetProperty(<paramref name="propertyName"/>).GetValue(null) — i.e.
    /// the singleton statics on either Teacher.App or Student.Agent.App.
    /// Shared.Wpf has no compile-time reference to either application's
    /// static type so this dynamic walk is how the chat-bubble template
    /// reaches App.Attachments without library coupling.</summary>
    private static void TryInvokeOnApp(string singletonName, System.Action<object> action)
    {
        var appType = Application.Current?.GetType();
        if (appType == null) return;
        var prop = appType.GetProperty(singletonName, BindingFlags.Public | BindingFlags.Static);
        var manager = prop?.GetValue(null);
        if (manager != null) action(manager);
    }
}
