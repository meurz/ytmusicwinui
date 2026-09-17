using System.Diagnostics;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.System.UserProfile;

namespace Music.Desktop.Localization;

/// <summary>Reads native PRI resources using an immutable language context for each app run.</summary>
public static class L
{
    private static readonly object Gate = new();
    private static readonly string[] SupportedLanguages = ["zh-CN", "zh-TW", "en-US"];
    private static ResourceManager? _manager;
    private static ResourceMap? _resources;
    private static ResourceContext? _context;
    private static ResourceContext? _englishContext;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");
    private static string _selectedLanguage = "system";
    private static string _languageTag = "en-US";

    public static string SelectedLanguage { get { lock (Gate) return _selectedLanguage; } }
    public static string LanguageTag { get { lock (Gate) return _languageTag; } }

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ytmusicwinui", "language.txt");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_manager is not null) return;
            _selectedLanguage = ReadPreference();
            _languageTag = _selectedLanguage == "system" ? ResolveSystemLanguage() : _selectedLanguage;
            _culture = CultureInfo.GetCultureInfo(_languageTag);
            var manager = new ResourceManager(Path.Combine(AppContext.BaseDirectory, "ytmusicwinui.pri"));
            var resources = manager.MainResourceMap.GetSubtree("Resources");
            var context = manager.CreateResourceContext();
            context.QualifierValues["Language"] = _languageTag;
            var englishContext = manager.CreateResourceContext();
            englishContext.QualifierValues["Language"] = "en-US";
            _resources = resources;
            _context = context;
            _englishContext = englishContext;
            _manager = manager;
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _culture;
            CultureInfo.DefaultThreadCurrentCulture = _culture;
            CultureInfo.DefaultThreadCurrentUICulture = _culture;
        }
    }

    public static string Get(string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (Gate)
        {
            Initialize();
            var value = _resources!.TryGetValue(key, _context!)?.ValueAsString
                ?? _resources.TryGetValue(key, _englishContext!)?.ValueAsString;
            if (value is null)
            {
                Debug.WriteLine("localization_missing_resource");
                return key;
            }
            if (arguments.Length == 0) return value;
            try { return string.Format(_culture, value, arguments); }
            catch (FormatException)
            {
                Debug.WriteLine("localization_invalid_format");
                return key;
            }
        }
    }

    /// <summary>Persists the next launch's selection without changing the current UI language.</summary>
    public static void SaveLanguage(string choice)
    {
        if (choice != "system" && !SupportedLanguages.Contains(choice, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported application language.", nameof(choice));
        lock (Gate)
        {
            var path = PreferencePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, choice, new System.Text.UTF8Encoding(false));
                File.Move(temporaryPath, path, overwrite: true);
                _selectedLanguage = choice;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Checks all three compiled translations without changing the app or saved selection.</summary>
    public static IReadOnlyDictionary<string, string> VerifyAllLanguages()
    {
        lock (Gate)
        {
            Initialize();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in SupportedLanguages)
            {
                var context = _manager!.CreateResourceContext();
                context.QualifierValues["Language"] = language;
                var candidate = _resources!.TryGetValue("SearchPlaceholder", context);
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.ValueAsString))
                    throw new InvalidOperationException($"Compiled localization is missing for {language}.");
                values.Add(language, candidate.ValueAsString);
            }
            if (values.Values.Distinct(StringComparer.Ordinal).Count() != SupportedLanguages.Length)
                throw new InvalidOperationException("Compiled language resources resolved to duplicate search placeholders.");
            return values;
        }
    }

    private static string ReadPreference()
    {
        try
        {
            var path = PreferencePath;
            if (!File.Exists(path)) return "system";
            var value = File.ReadAllText(path).Trim();
            return value == "system" || SupportedLanguages.Contains(value, StringComparer.Ordinal) ? value : "system";
        }
        catch (IOException) { Debug.WriteLine("localization_preference_read_failed"); return "system"; }
        catch (UnauthorizedAccessException) { Debug.WriteLine("localization_preference_read_failed"); return "system"; }
    }

    private static string ResolveSystemLanguage()
    {
        string language;
        try { language = GlobalizationPreferences.Languages.FirstOrDefault() ?? CultureInfo.InstalledUICulture.Name; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            language = CultureInfo.InstalledUICulture.Name;
        }
        var parts = language.Split('-');
        if (!parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase)) return "en-US";
        return parts.Any(part => part.Equals("Hant", StringComparison.OrdinalIgnoreCase)
            || part.Equals("TW", StringComparison.OrdinalIgnoreCase)
            || part.Equals("HK", StringComparison.OrdinalIgnoreCase)
            || part.Equals("MO", StringComparison.OrdinalIgnoreCase)) ? "zh-TW" : "zh-CN";
    }
}
