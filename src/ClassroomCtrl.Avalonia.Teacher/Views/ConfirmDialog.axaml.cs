using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassroomCtrl.Avalonia.Teacher.Views;

/// <summary>
/// TT-5-C — the power-action confirmation modal, injected into
/// <c>StudentCommandController</c> as its confirm delegate. Replaces the shipped
/// Windows Teacher's WPF <c>MessageBox.Show(..YesNo, Warning)</c> (Windows-only).
/// The confirm POLICY (which actions prompt, and that a decline sends nothing) is
/// owned + headless-gated at the controller; this window is the thin Yes/No surface,
/// LIVE-verified.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>True iff the teacher clicked Confirm. Read after ShowDialog returns.</summary>
    public bool Confirmed { get; private set; }

    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string message) : this() => MessageText.Text = message;

    private void Yes_Click(object? sender, RoutedEventArgs e) { Confirmed = true; Close(); }
    private void No_Click(object? sender, RoutedEventArgs e) { Confirmed = false; Close(); }

    /// <summary>Show the modal over <paramref name="owner"/> and return the teacher's choice.
    /// Must be called on the UI thread (it is — the controller runs from a UI event handler).</summary>
    public static async Task<bool> ShowAsync(Window owner, string message)
    {
        var dlg = new ConfirmDialog(message);
        await dlg.ShowDialog(owner);
        return dlg.Confirmed;
    }
}
