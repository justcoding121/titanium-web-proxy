using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;

namespace Titanium.Inspector.Localization;

/// <summary>Looks up a catalog id and refreshes the target when the language changes.</summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = LanguageService.Get(Key);
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget provide
            && provide.TargetObject is AvaloniaObject target
            && provide.TargetProperty is AvaloniaProperty property)
        {
            LanguageService.Instance.Track(target, property, Key);
        }

        return text;
    }
}
