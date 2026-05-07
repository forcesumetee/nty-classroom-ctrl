using System.Collections.Generic;
using System.Globalization;

namespace ClassroomCtrl.Shared.Localization;

/// <summary>
/// Localization manager. UI-agnostic core.
/// 
/// WPF-specific application of strings is done via Loc.LanguageChanged callback,
/// hooked up by each WPF app (Teacher/Agent) in its App.xaml.cs.
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "en";

    public static readonly Dictionary<string, string> SupportedLanguages = new()
    {
        ["en"] = "English",
        ["th"] = "ไทย",
        ["zh"] = "中文",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["de"] = "Deutsch",
        ["ru"] = "Русский",
        ["vi"] = "Tiếng Việt",
    };

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>Fires when language changes; WPF apps subscribe to refresh Application.Resources.</summary>
    public static event System.Action? LanguageChanged;

    private static Dictionary<string, Dictionary<string, string>>? _data;

    public static void Initialize(string? preferredLanguage = null)
    {
        _data = LocalizationData.BuildAll();

        var lang = preferredLanguage;
        if (string.IsNullOrEmpty(lang) || !SupportedLanguages.ContainsKey(lang))
        {
            var os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            lang = SupportedLanguages.ContainsKey(os) ? os : DefaultLanguage;
        }
        CurrentLanguage = lang;
        LanguageChanged?.Invoke();
    }

    public static void SetLanguage(string lang)
    {
        if (!SupportedLanguages.ContainsKey(lang)) return;
        if (lang == CurrentLanguage) return;
        CurrentLanguage = lang;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key)
    {
        if (_data == null) return $"[{key}]";

        if (_data.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            return v;

        if (_data.TryGetValue(DefaultLanguage, out var en) && en.TryGetValue(key, out var ev) && !string.IsNullOrEmpty(ev))
            return ev;

        return $"[{key}]";
    }

    /// <summary>
    /// Defensive overload: returns <paramref name="fallback"/> when the key is missing or
    /// the resolved value would be the bracketed `[Key]` placeholder. Use this when
    /// rendering critical UI text that must never display the raw key.
    /// </summary>
    public static string Get(string key, string fallback)
    {
        var v = Get(key);
        if (string.IsNullOrEmpty(v) || v == $"[{key}]")
            return fallback ?? "";
        return v;
    }

    public static string Format(string key, params object[] args)
    {
        var fmt = Get(key);
        try { return string.Format(fmt, args); }
        catch { return fmt; }
    }

    /// <summary>
    /// Returns all key-value pairs for current language (with English fallback for missing keys).
    /// Used by WPF apps to populate Application.Resources.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateCurrent()
    {
        if (_data == null) yield break;

        _data.TryGetValue(CurrentLanguage, out var dict);
        _data.TryGetValue(DefaultLanguage, out var fallback);

        var allKeys = new HashSet<string>();
        if (dict != null) foreach (var k in dict.Keys) allKeys.Add(k);
        if (fallback != null) foreach (var k in fallback.Keys) allKeys.Add(k);

        foreach (var key in allKeys)
        {
            string value;
            if (dict != null && dict.TryGetValue(key, out var v)) value = v;
            else if (fallback != null && fallback.TryGetValue(key, out var fv)) value = fv;
            else continue;

            yield return new KeyValuePair<string, string>(key, value);
        }
    }
}