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

    /// <summary>Checks every compiled key, language qualifier and format argument set.</summary>
    public static IReadOnlyDictionary<string, string> VerifyAllLanguages()
    {
        lock (Gate)
        {
            Initialize();
            if (_resources!.ResourceCount == 0)
                throw new InvalidOperationException("The compiled resource map is empty.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in SupportedLanguages)
            {
                var context = _manager!.CreateResourceContext();
                context.QualifierValues["Language"] = language;
                for (uint index = 0; index < _resources.ResourceCount; index++)
                {
                    var entry = _resources.GetValueByIndex(index, _englishContext!);
                    var candidate = _resources.TryGetValue(entry.Key, context);
                    if (candidate is null || string.IsNullOrWhiteSpace(candidate.ValueAsString))
                        throw new InvalidOperationException($"Compiled localization is missing for {language}.");
                    var qualifier = candidate.QualifierValues.FirstOrDefault(pair =>
                        pair.Key.Equals("Language", StringComparison.OrdinalIgnoreCase)).Value;
                    if (!string.Equals(qualifier, language, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Compiled localization fell back from {language}.");
                    if (!FormatArgumentIndices(entry.Value.ValueAsString).SequenceEqual(
                        FormatArgumentIndices(candidate.ValueAsString)))
                        throw new InvalidOperationException($"Compiled format arguments differ for {language}.");
                }
                values.Add(language, _resources.GetValue("SearchPlaceholder", context).ValueAsString);
            }
            return values;
        }
    }

    private static IEnumerable<int> FormatArgumentIndices(string format)
    {
        // Let the framework validate the complete composite-format grammar first.
        _ = System.Text.CompositeFormat.Parse(format);
        var indices = new SortedSet<int>();
        for (int position = 0; position < format.Length; position++)
        {
            if (format[position] != '{') continue;
            if (position + 1 < format.Length && format[position + 1] == '{') { position++; continue; }
            int start = ++position;
            while (position < format.Length && char.IsAsciiDigit(format[position])) position++;
            indices.Add(int.Parse(format.AsSpan(start, position - start), CultureInfo.InvariantCulture));
        }
        return indices;
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
