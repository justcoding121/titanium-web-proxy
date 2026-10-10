using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Titanium.Inspector.Localization;

/// <summary>
/// UI string catalog. Does not change <see cref="CultureInfo.CurrentCulture"/> or
/// <see cref="CultureInfo.CurrentUICulture"/>. Automatic mode reads the OS UI language
/// (<see cref="OsUiLanguage"/>), not the process locale alone.
/// </summary>
public sealed class LanguageService : INotifyPropertyChanged
{
    public const string AutomaticSetting = "auto";
    public const string English = "en";

    private static readonly string[] Cultures =
    [
        "en", "ar", "zh-Hans", "zh-Hant", "cs", "da", "nl", "fi", "fr", "de",
        "el", "he", "hi", "hu", "id", "it", "ja", "ko", "nb", "pl",
        "pt-BR", "pt-PT", "ru", "es", "sv", "th", "tr", "uk", "vi", "fa",
    ];

    /// <summary>Options menu rows: setting value, then the name shown in the menu.</summary>
    public static readonly (string Setting, string Endonym)[] MenuLanguages =
    [
        (AutomaticSetting, ""),
        ("en", "English"),
        ("ar", "العربية"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("cs", "Čeština"),
        ("da", "Dansk"),
        ("nl", "Nederlands"),
        ("fi", "Suomi"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("el", "Ελληνικά"),
        ("he", "עברית"),
        ("hi", "हिन्दी"),
        ("hu", "Magyar"),
        ("id", "Bahasa Indonesia"),
        ("it", "Italiano"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("nb", "Norsk bokmål"),
        ("pl", "Polski"),
        ("pt-BR", "Português (Brasil)"),
        ("pt-PT", "Português (Portugal)"),
        ("ru", "Русский"),
        ("es", "Español"),
        ("sv", "Svenska"),
        ("th", "ไทย"),
        ("tr", "Türkçe"),
        ("uk", "Українська"),
        ("vi", "Tiếng Việt"),
        ("fa", "فارسی"),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pt"] = "pt-BR",
        ["nn"] = "nb",
        ["no"] = "nb",
        ["zh"] = "zh-Hans",
        ["iw"] = "he",
        ["in"] = "id",
    };

    private readonly List<TargetBinding> _targets = new();
    private Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private Dictionary<string, string> _english = new(StringComparer.Ordinal);

    static LanguageService()
    {
        Instance.Load(English);
    }

    private LanguageService()
    {
    }

    public static LanguageService Instance { get; } = new();

    public static IReadOnlyList<string> ShippedCultures => Cultures;

    /// <summary>When set, <see cref="Apply"/> always loads this culture (test host).</summary>
    public static string? PinnedCulture { get; private set; }

    public string ActiveCulture { get; private set; } = English;

    public FlowDirection FlowDirection { get; private set; } = FlowDirection.LeftToRight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    public static void PinForTests(string culture)
    {
        PinnedCulture = culture;
        Instance.Load(culture);
    }

    public static void Apply(string? uiLanguageSetting)
    {
        var culture = PinnedCulture ?? ResolveCultureName(uiLanguageSetting);
        Instance.Load(culture);
    }

    public static string Get(string key) => Instance.Lookup(key);

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>
    /// Map a settings value or OS UI culture onto a shipped catalog. Does not assign the thread culture.
    /// </summary>
    public static string ResolveCultureName(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)
            || setting.Equals(AutomaticSetting, StringComparison.OrdinalIgnoreCase))
        {
            return MatchCulture(OsUiLanguage.Detect());
        }

        if (TryCatalog(setting, out var direct))
        {
            return direct;
        }

        try
        {
            return MatchCulture(CultureInfo.GetCultureInfo(setting));
        }
        catch (CultureNotFoundException)
        {
            return English;
        }
    }

    public static bool IsRightToLeft(string cultureName)
    {
        try
        {
            return CultureInfo.GetCultureInfo(cultureName).TextInfo.IsRightToLeft;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    public void Load(string cultureName)
    {
        var culture = TryCatalog(cultureName, out var matched) ? matched : English;
        _english = ReadCatalog(English);
        _active = culture == English
            ? _english
            : ReadCatalog(culture);
        ActiveCulture = culture;
        FlowDirection = IsRightToLeft(culture) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RefreshTargets();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveCulture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keep a window's flow direction aligned with the active culture.</summary>
    public static void AttachWindow(Window window)
    {
        window.FlowDirection = Instance.FlowDirection;
        void OnLanguageChanged(object? sender, EventArgs e) =>
            window.FlowDirection = Instance.FlowDirection;
        Instance.LanguageChanged += OnLanguageChanged;
        window.Closed += (_, _) => Instance.LanguageChanged -= OnLanguageChanged;
    }

    internal void Track(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        _targets.Add(new TargetBinding(new WeakReference<AvaloniaObject>(target), property, key));
    }

    internal static Dictionary<string, string> ReadCatalog(string culture)
    {
        var stream = OpenCatalog(culture);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using (stream)
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return map is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
    }

    internal static Stream? OpenCatalog(string culture)
    {
        var assembly = typeof(LanguageService).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"strings.{culture}.json", StringComparison.OrdinalIgnoreCase));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }

    private string Lookup(string key)
    {
        if (_active.TryGetValue(key, out var value) && value.Length > 0)
        {
            return value;
        }

        if (_english.TryGetValue(key, out var english) && english.Length > 0)
        {
            return english;
        }

        return key;
    }

    private void RefreshTargets()
    {
        for (var i = _targets.Count - 1; i >= 0; i--)
        {
            var binding = _targets[i];
            if (!binding.Target.TryGetTarget(out var target))
            {
                _targets.RemoveAt(i);
                continue;
            }

            target.SetCurrentValue(binding.Property, Lookup(binding.Key));
        }
    }

    private static string MatchCulture(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (TryCatalog(current.Name, out var name))
            {
                return name;
            }
        }

        return English;
    }

    private static bool TryCatalog(string name, out string canonical)
    {
        if (Aliases.TryGetValue(name, out var alias))
        {
            canonical = alias;
            return true;
        }

        foreach (var culture in Cultures)
        {
            if (culture.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                canonical = culture;
                return true;
            }
        }

        canonical = English;
        return false;
    }

    private readonly record struct TargetBinding(
        WeakReference<AvaloniaObject> Target,
        AvaloniaProperty Property,
        string Key);
}
