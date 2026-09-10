using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Titanium.Inspector.DesktopProbe;

/// <summary>AutomationId robot (same pattern as E2E InspectorUiRobot, no MSTest dependency).</summary>
public sealed class ProbeUiRobot(Control root)
{
    public T Find<T>(string automationId) where T : Control
    {
        var match = FindControl<T>(automationId);
        if (match is null)
            throw new InvalidOperationException($"Control '{automationId}' of type {typeof(T).Name} not found.");
        return match;
    }

    public bool TryFind<T>(string automationId, out T? control) where T : Control
    {
        control = FindControl<T>(automationId);
        return control is not null;
    }

    public void Click(string automationId)
    {
        var control = Find<Control>(automationId);
        switch (control)
        {
            case Button { Command: { } cmd } button:
                if (!cmd.CanExecute(button.CommandParameter))
                    throw new InvalidOperationException($"Command not executable: {automationId}");
                cmd.Execute(button.CommandParameter);
                break;
            case MenuItem { Command: { } menuCmd } menu:
                if (!menuCmd.CanExecute(menu.CommandParameter))
                    throw new InvalidOperationException($"Menu command not executable: {automationId}");
                menuCmd.Execute(menu.CommandParameter);
                break;
            case Button button:
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case TabItem tab when tab.Parent is TabControl tabs:
                tabs.SelectedItem = tab;
                break;
            case MenuItem menu:
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                break;
            default:
                control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
        }
    }

    public void SetText(string automationId, string text) =>
        Find<TextBox>(automationId).Text = text;

    public void SetCheck(string automationId, bool value)
    {
        if (TryFind<CheckBox>(automationId, out var check) && check is not null)
        {
            check.IsChecked = value;
            return;
        }

        if (TryFind<MenuItem>(automationId, out var menu) && menu is not null)
        {
            if (menu.Command is { } cmd && menu.IsChecked != value)
            {
                if (!cmd.CanExecute(menu.CommandParameter))
                    throw new InvalidOperationException($"Menu command not executable: {automationId}");
                cmd.Execute(menu.CommandParameter);
                return;
            }

            menu.IsChecked = value;
            return;
        }

        throw new InvalidOperationException($"Checkable control '{automationId}' not found.");
    }

    public bool? GetCheck(string automationId)
    {
        if (TryFind<CheckBox>(automationId, out var check) && check is not null)
            return check.IsChecked;
        if (TryFind<MenuItem>(automationId, out var menu) && menu is not null)
            return menu.IsChecked;
        return null;
    }

    /// <summary>Raise a key on a control by AutomationId (e.g. Delete on SessionsGrid).</summary>
    public void RaiseKey(string automationId, Key key)
    {
        var control = Find<Control>(automationId);
        control.Focus();
        control.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            Source = control,
        });
    }

    /// <summary>Open the session grid context menu so Ctx* items are reachable.</summary>
    public void OpenSessionsContextMenu()
    {
        if (!TryFind<DataGrid>("SessionsGrid", out var grid) || grid is null)
            throw new InvalidOperationException("SessionsGrid not found.");
        if (grid.ContextMenu is null)
            throw new InvalidOperationException("SessionsGrid has no ContextMenu.");
        grid.ContextMenu.Open(grid);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Click a SessionsGrid context-menu item by AutomationId (searches the open menu, then the window).
    /// </summary>
    public void ClickSessionsContextItem(string automationId)
    {
        if (!TryFind<DataGrid>("SessionsGrid", out var grid) || grid is null)
            throw new InvalidOperationException("SessionsGrid not found.");
        if (grid.ContextMenu is null)
            throw new InvalidOperationException("SessionsGrid has no ContextMenu.");

        grid.ContextMenu.Open(grid);
        Dispatcher.UIThread.RunJobs();

        MenuItem? item = FindMenuItemIn(grid.ContextMenu, automationId)
                         ?? FindControl<MenuItem>(automationId);
        if (item is null)
            throw new InvalidOperationException($"Context menu item '{automationId}' not found.");

        if (item.Command is { } cmd)
        {
            if (!cmd.CanExecute(item.CommandParameter))
                throw new InvalidOperationException($"Context menu command not executable: {automationId}");
            cmd.Execute(item.CommandParameter);
            return;
        }

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static MenuItem? FindMenuItemIn(Control root, string automationId)
    {
        static bool IdMatch(Control c, string id) =>
            string.Equals(AutomationProperties.GetAutomationId(c), id, StringComparison.Ordinal);

        foreach (var mi in root.GetLogicalDescendants().OfType<MenuItem>())
        {
            if (IdMatch(mi, automationId))
                return mi;
        }

        if (root is ItemsControl items)
        {
            foreach (var child in items.Items)
            {
                if (child is MenuItem mi && IdMatch(mi, automationId))
                    return mi;
            }
        }

        return root.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(c => IdMatch(c, automationId));
    }

    /// <summary>Click an AutomationId inside any non-main window (dialogs).</summary>
    public static bool TryClickInOtherWindows(Window mainWindow, string automationId)
    {
        foreach (var window in OtherWindows(mainWindow))
        {
            var robot = new ProbeUiRobot(window);
            if (!robot.TryFind<Control>(automationId, out _))
                continue;
            robot.Click(automationId);
            return true;
        }

        return false;
    }

    public static bool HasOtherWindows(Window mainWindow) => OtherWindows(mainWindow).Any();

    public static IEnumerable<Window> OtherWindows(Window mainWindow)
    {
        var seen = new HashSet<Window>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, mainWindow) && seen.Add(window))
                    yield return window;
            }
        }
    }

    /// <summary>Find a dialog window by AutomationId (may be outside the main logical tree).</summary>
    public static Window? FindWindowById(string automationId)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.Windows.FirstOrDefault(w =>
            string.Equals(AutomationProperties.GetAutomationId(w), automationId, StringComparison.Ordinal));
    }

    public static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > limit)
                throw new TimeoutException("Timed out waiting for UI condition.");
            await Task.Delay(40).ConfigureAwait(true);
        }
    }

    private T? FindControl<T>(string automationId) where T : Control
    {
        static bool IdMatch(Control c, string id) =>
            string.Equals(AutomationProperties.GetAutomationId(c), id, StringComparison.Ordinal);

        var logical = root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => IdMatch(c, automationId));
        if (logical is not null)
            return logical;

        return root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => IdMatch(c, automationId));
    }
}
