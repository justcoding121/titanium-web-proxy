using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
