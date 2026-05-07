using ClassroomCtrl.Shared.Localization;
using Microsoft.Win32;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace ClassroomCtrl.Student.Agent.Setup;

/// <summary>
/// Phase 14.2 — Admin password gate for connection settings. Default password
/// is "nty2026" but a school can override by writing a SHA-256 hash to
/// HKLM\Software\NTY\ClassroomCtrl\AdminPasswordHash. Phase 5D: also checks
/// HKCU first so a teacher's locally-set hash works without admin rights.
/// </summary>
public partial class AdminPasswordDialog : Window
{
    private const string DefaultPassword = "nty2026";
    private const string RegPath = @"Software\NTY\ClassroomCtrl";
    private const string RegHashKey = "AdminPasswordHash";

    public bool Authenticated { get; private set; }

    public AdminPasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        // Trim — pairs with Teacher's AdminPasswordSettingsDialog.Save_Click which also
        // trims, so accidental whitespace can't desync the two hashes.
        var entered = (PasswordBox.Password ?? "").Trim();
        if (string.IsNullOrEmpty(entered))
        {
            ErrorText.Text = Loc.Get("Msg_PasswordIncorrect");
            return;
        }

        var enteredHash = Hash(entered);
        var storedHash = ReadStoredHash();
        var defaultHash = Hash(DefaultPassword);

        // Exclusive rule (security): once a teacher sets a custom hash the built-in
        // default ("nty2026") MUST NOT bypass it. The previous OR rule let the default
        // unlock connection settings even after the school had configured a real
        // password — clearly a footgun. Default applies only on first-run / unset state.
        // Ordinal case-insensitive — both writers emit lowercase hex but a manually
        // deployed registry value could be uppercase from PowerShell tooling.
        bool authenticated;
        if (string.IsNullOrEmpty(storedHash))
        {
            authenticated = string.Equals(enteredHash, defaultHash,
                                          StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            authenticated = string.Equals(enteredHash, storedHash,
                                          StringComparison.OrdinalIgnoreCase);
        }

        if (authenticated)
        {
            Authenticated = true;
            DialogResult = true;
            Close();
        }
        else
        {
            ErrorText.Text = Loc.Get("Msg_PasswordIncorrect");
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string ReadStoredHash()
    {
        // HKCU first (Teacher's per-user override / dev box), then HKLM (GPO deploy).
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(RegPath);
            var perUser = hkcu?.GetValue(RegHashKey) as string;
            if (!string.IsNullOrEmpty(perUser)) return perUser;
        }
        catch { /* fall through */ }
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(RegPath);
            return hklm?.GetValue(RegHashKey) as string ?? "";
        }
        catch { return ""; }
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(64);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
