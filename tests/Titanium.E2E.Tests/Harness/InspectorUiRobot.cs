using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

public sealed class InspectorUiRobot(Control root)
{
    public T Find<T>(string automationId) where T : Control
    {
        var match = FindControl<T>(automationId);
        Assert.IsNotNull(match, $"Control '{automationId}' of type {typeof(T).Name} not found.");
        return match!;
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
                Assert.IsTrue(cmd.CanExecute(button.CommandParameter), $"Command not executable: {automationId}");
                cmd.Execute(button.CommandParameter);
                break;
            case MenuItem { Command: { } menuCmd } menu:
                Assert.IsTrue(menuCmd.CanExecute(menu.CommandParameter), $"Menu command not executable: {automationId}");
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

    public void SetText(string automationId, string text) => Find<TextBox>(automationId).Text = text;

    public void SetCheck(string automationId, bool value)
    {
        if (TryFind<CheckBox>(automationId, out var check) && check is not null)
        {
            check.IsChecked = value;
            return;
        }

        if (TryFind<MenuItem>(automationId, out var menu) && menu is not null)
        {
            // Preference menus use Mode=OneWay + Command — assigning IsChecked alone won't update the VM.
            if (menu.Command is { } cmd && menu.IsChecked != value)
            {
                Assert.IsTrue(cmd.CanExecute(menu.CommandParameter), $"Menu command not executable: {automationId}");
                cmd.Execute(menu.CommandParameter);
                return;
            }

            menu.IsChecked = value;
            return;
        }

        Assert.Fail($"Checkable control '{automationId}' (CheckBox/MenuItem) not found.");
    }

    public static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(8);
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > limit)
            {
                Assert.Fail("Timed out waiting for UI condition.");
            }

            await Task.Delay(40);
        }
    }

    private T? FindControl<T>(string automationId) where T : Control
    {
        static bool IdMatch(Control c, string id) =>
            string.Equals(AutomationProperties.GetAutomationId(c), id, StringComparison.Ordinal);

        // MenuItem children live on the logical tree before the popup is opened.
        var logical = root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => IdMatch(c, automationId));
        if (logical is not null)
        {
            return logical;
        }

        return root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => IdMatch(c, automationId));
    }
}
