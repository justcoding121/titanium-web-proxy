using Avalonia.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorShortcutsTests
{
    [TestMethod]
    public void Match_GlobalKeys_IgnoreFocus_AndLetterKeysStayOnTheGrid()
    {
        Assert.AreEqual(
            ShortcutAction.ToggleCapture,
            InspectorShortcuts.Match(Key.F12, KeyModifiers.None, gridFocused: false, textInputFocused: true, macOs: false));

        Assert.AreEqual(
            ShortcutAction.FocusSearch,
            InspectorShortcuts.Match(Key.F, KeyModifiers.Control, gridFocused: false, textInputFocused: true, macOs: false));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.F, KeyModifiers.Meta, gridFocused: true, textInputFocused: false, macOs: false));
        Assert.AreEqual(
            ShortcutAction.FocusSearch,
            InspectorShortcuts.Match(Key.F, KeyModifiers.Meta, gridFocused: false, textInputFocused: true, macOs: true));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.F, KeyModifiers.Control, gridFocused: true, textInputFocused: false, macOs: true));

        Assert.AreEqual(
            ShortcutAction.ClearSessions,
            InspectorShortcuts.Match(Key.Delete, KeyModifiers.Control | KeyModifiers.Shift, gridFocused: false, textInputFocused: true, macOs: false));
        Assert.AreEqual(
            ShortcutAction.ClearSessions,
            InspectorShortcuts.Match(Key.Delete, KeyModifiers.Meta | KeyModifiers.Shift, gridFocused: false, textInputFocused: false, macOs: true));

        Assert.AreEqual(
            ShortcutAction.Replay,
            InspectorShortcuts.Match(Key.R, KeyModifiers.None, gridFocused: true, textInputFocused: false, macOs: false));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.R, KeyModifiers.None, gridFocused: true, textInputFocused: true, macOs: false));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.R, KeyModifiers.None, gridFocused: false, textInputFocused: false, macOs: true));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.R, KeyModifiers.Control, gridFocused: true, textInputFocused: false, macOs: false));

        Assert.AreEqual(
            ShortcutAction.RemoveSelected,
            InspectorShortcuts.Match(Key.Delete, KeyModifiers.None, gridFocused: true, textInputFocused: false, macOs: false));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.Delete, KeyModifiers.None, gridFocused: true, textInputFocused: true, macOs: false));
        Assert.AreEqual(
            ShortcutAction.None,
            InspectorShortcuts.Match(Key.Delete, KeyModifiers.None, gridFocused: false, textInputFocused: false, macOs: false));
    }

    [TestMethod]
    public void Sections_UseTheSameGestures_OnWindowsAndMac()
    {
        var windows = InspectorShortcuts.Sections(macOs: false).SelectMany(section => section.Entries).ToArray();
        var mac = InspectorShortcuts.Sections(macOs: true).SelectMany(section => section.Entries).ToArray();

        Assert.AreEqual("F12", Gesture(windows, ShortcutAction.ToggleCapture));
        Assert.AreEqual("Delete", Gesture(windows, ShortcutAction.RemoveSelected));
        Assert.AreEqual("R", Gesture(windows, ShortcutAction.Replay));
        Assert.AreEqual("Ctrl+Shift+Delete", Gesture(windows, ShortcutAction.ClearSessions));
        Assert.AreEqual("Ctrl+F", Gesture(windows, ShortcutAction.FocusSearch));

        Assert.AreEqual("Cmd+Shift+Delete", Gesture(mac, ShortcutAction.ClearSessions));
        Assert.AreEqual("Cmd+F", Gesture(mac, ShortcutAction.FocusSearch));
        Assert.AreEqual(3, InspectorShortcuts.Sections(false).Count);
    }

    [TestMethod]
    public void DisabledCommand_IgnoresItsKey_AndToggleCaptureRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            interception,
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker());
        var status = vm.StatusText;

        Assert.IsFalse(vm.TryHandleShortcut(ShortcutAction.ClearSessions));
        Assert.IsFalse(vm.TryHandleShortcut(ShortcutAction.RemoveSelected));
        Assert.IsFalse(vm.TryHandleShortcut(ShortcutAction.Replay));
        Assert.IsFalse(vm.TryHandleShortcut(ShortcutAction.FocusSearch));
        Assert.AreEqual(status, vm.StatusText);
        Assert.IsTrue(vm.Capturing);

        Assert.IsTrue(vm.TryHandleShortcut(ShortcutAction.ToggleCapture));
        Assert.IsFalse(vm.Capturing);
        Assert.IsFalse(interception.Capturing);

        vm.OpenKeyboardShortcutsCommand.Execute(null);
        StringAssert.Contains(vm.StatusText, "main window");
    }

    private static string Gesture(InspectorShortcuts.ShortcutEntry[] entries, ShortcutAction action) =>
        entries.Single(entry => entry.Action == action).Gesture;
}
