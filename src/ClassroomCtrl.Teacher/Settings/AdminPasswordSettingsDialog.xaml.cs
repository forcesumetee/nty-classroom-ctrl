using ClassroomCtrl.Shared.Localization;
using Microsoft.Win32;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace ClassroomCtrl.Teacher.Settings;

/// <summary>
/// Phase 5D — Teacher-side dialog for setting the admin password that gates
/// student-PC connection settings (read by Student.Agent's AdminPasswordDialog).
/// Saves a SHA-256 hash to HKCU\Software\NTY\ClassroomCtrl\AdminPasswordHash;
/// Student.Agent reads HKCU first, then HKLM, then falls back to the built-in
/// "nty2026" default — so deploying the same hash via GPO continues to work
/// for class-wide setup.
/// </summary>
public partial class AdminPasswordSettingsDialog : Window
{
    private const string RegPath = @"Software\NTY\ClassroomCtrl";
    private const string RegHashKey = "AdminPasswordHash";

    public AdminPasswordSettingsDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NewPasswordBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Trim — accidental leading/trailing whitespace from clipboard paste was the
        // most likely cause of "password mismatch" reports between Teacher save and
        // Student.Agent verify (both apps now trim consistently).
        var pw = (NewPasswordBox.Password ?? "").Trim();
        var confirm = (ConfirmPasswordBox.Password ?? "").Trim();

        if (string.IsNullOrWhiteSpace(pw))
        {
            ShowStatus(Loc.Get("Msg_PasswordEmpty"), error: true);
            return;
        }
        if (pw.Length < 4)
        {
            ShowStatus(Loc.Get("Msg_PasswordTooShort"), error: true);
            return;
        }
        if (pw != confirm)
        {
            ShowStatus(Loc.Get("Msg_PasswordMismatch"), error: true);
            return;
        }

        var hash = Hash(pw);
        if (!TryWriteHash(hash, out var err))
        {
            ShowStatus(string.Format(Loc.Get("Msg_PasswordSaveFailed"), err), error: true);
            return;
        }

        ShowStatus(Loc.Get("Msg_PasswordSaved"), error: false);
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Clearing the registry value forces fallback to the built-in default ("nty2026").
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.DeleteValue(RegHashKey, throwOnMissingValue: false);
        }
        catch { /* HKCU write should never fail; ignore on best-effort */ }
        ShowStatus(Loc.Get("Msg_PasswordReset"), error: false);
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Phase 8 (Bug F) — emit a .reg file containing the saved hash under HKLM so the
    /// customer can side-channel it to every Student PC. Uses HKLM (not HKCU) because
    /// Student.Agent reads HKCU first then HKLM, and the operator deploying via .reg is
    /// almost always running it elevated against the local machine, not their own profile.
    /// HKLM also makes the password apply to every user account on a shared classroom PC.
    /// </summary>
    private void ExportReg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            var hash = key?.GetValue(RegHashKey) as string;
            if (string.IsNullOrEmpty(hash))
            {
                ShowStatus(Loc.Get("Msg_NoHashToExport"), error: true);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = Loc.Get("Btn_ExportRegFile"),
                Filter = "Registry file (*.reg)|*.reg",
                FileName = "ClassroomCtrl-AdminPassword.reg",
            };
            if (dlg.ShowDialog() != true) return;

            // .reg files are ANSI/UTF-16 by Windows convention; "Version 5.00" is UTF-16 LE
            // with a BOM, but a plain ASCII header also imports fine on Windows 7+ which is
            // every classroom OS we ship to. SHA-256 hashes are hex-only so no escaping needed.
            var content =
                "Windows Registry Editor Version 5.00\r\n\r\n" +
                "[HKEY_LOCAL_MACHINE\\Software\\NTY\\ClassroomCtrl]\r\n" +
                $"\"{RegHashKey}\"=\"{hash}\"\r\n";
            File.WriteAllText(dlg.FileName, content, Encoding.Unicode);

            ShowStatus(Loc.Get("Msg_RegFileExported"), error: false);
        }
        catch (Exception ex)
        {
            ShowStatus(string.Format(Loc.Get("Msg_PasswordSaveFailed"), ex.Message), error: true);
        }
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        // Use semantic-named resource keys so live theme/branding swaps update both
        // the error red and the success indication. Success uses Text.Primary so it
        // stays readable on either Light or Dark surface — the green tint of
        // Accent.Success had insufficient contrast on Dark backgrounds.
        StatusText.Foreground = error
            ? (Brush)(Application.Current.Resources["Accent.Danger"] ?? Brushes.OrangeRed)
            : (Brush)(Application.Current.Resources["Text.Primary"] ?? Brushes.Black);
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(64);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static bool TryWriteHash(string hash, out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(RegHashKey, hash, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
