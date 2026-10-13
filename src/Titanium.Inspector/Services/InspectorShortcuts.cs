using Avalonia.Input;

namespace Titanium.Inspector.Services;

/// <summary>
/// Keyboard shortcuts for the main window. Letter keys apply only while the session
/// grid is focused and the key did not land in a text box. Cmd is the platform key on macOS.
/// </summary>
public static class InspectorShortcuts
{
    public const string GroupCapture = "shortcuts.capture";
    public const string GroupSessions = "shortcuts.sessions";
    public const string GroupWindow = "shortcuts.window";

    public readonly record struct ShortcutEntry(string ActionKey, string Gesture, ShortcutAction Action);

    public readonly record struct ShortcutSection(string TitleKey, IReadOnlyList<ShortcutEntry> Entries);

    public static KeyModifiers PlatformModifier(bool macOs) =>
        macOs ? KeyModifiers.Meta : KeyModifiers.Control;

    public static IReadOnlyList<ShortcutSection> Sections(bool macOs) =>
    [
        new(GroupCapture,
        [
            new("shortcuts.toggleCapture", Format(Key.F12, KeyModifiers.None, macOs), ShortcutAction.ToggleCapture),
        ]),
        new(GroupSessions,
        [
            new("menu.removeSelected", Format(Key.Delete, KeyModifiers.None, macOs), ShortcutAction.RemoveSelected),
            new("ctx.replay", Format(Key.R, KeyModifiers.None, macOs), ShortcutAction.Replay),
            new("menu.clearSessions", Format(Key.Delete, PlatformModifier(macOs) | KeyModifiers.Shift, macOs), ShortcutAction.ClearSessions),
        ]),
        new(GroupWindow,
        [
            new("shortcuts.focusSearch", Format(Key.F, PlatformModifier(macOs), macOs), ShortcutAction.FocusSearch),
        ]),
    ];

    public static ShortcutAction Match(
        Key key,
        KeyModifiers modifiers,
        bool gridFocused,
        bool textInputFocused,
        bool macOs)
    {
        if (key == Key.F12 && modifiers == KeyModifiers.None)
            return ShortcutAction.ToggleCapture;

        var platform = PlatformModifier(macOs);
        if (key == Key.F && modifiers == platform)
            return ShortcutAction.FocusSearch;

        if (key == Key.Delete && modifiers == (platform | KeyModifiers.Shift))
            return ShortcutAction.ClearSessions;

        if (modifiers != KeyModifiers.None || textInputFocused || !gridFocused)
            return ShortcutAction.None;

        return key switch
        {
            Key.R => ShortcutAction.Replay,
            Key.Delete => ShortcutAction.RemoveSelected,
            _ => ShortcutAction.None,
        };
    }

    public static string Format(Key key, KeyModifiers modifiers, bool macOs)
    {
        var text = "";
        if (modifiers.HasFlag(KeyModifiers.Meta) || (modifiers.HasFlag(KeyModifiers.Control) && !macOs))
            text += macOs ? "Cmd+" : "Ctrl+";
        else if (macOs && modifiers.HasFlag(KeyModifiers.Control))
            text += "Ctrl+";

        if (modifiers.HasFlag(KeyModifiers.Shift))
            text += "Shift+";
        if (modifiers.HasFlag(KeyModifiers.Alt))
            text += macOs ? "Option+" : "Alt+";

        text += key switch
        {
            Key.Delete => "Delete",
            Key.F12 => "F12",
            Key.F => "F",
            Key.R => "R",
            _ => key.ToString(),
        };
        return text;
    }
}

public enum ShortcutAction
{
    None = 0,
    ToggleCapture,
    FocusSearch,
    ClearSessions,
    Replay,
    RemoveSelected,
}
