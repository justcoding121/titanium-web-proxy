using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

/// <summary>Small comment editor. Cancel returns null. Save returns the text, including empty.</summary>
public sealed class CommentPrompt : Window
{
    private readonly TextBox _box;
    private string? _result;

    private CommentPrompt(string? current, int maxLength)
    {
        Title = LanguageService.Get("comment.title");
        Width = 440;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        _box = new TextBox
        {
            Text = current ?? "",
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxLength = maxLength,
            MinHeight = 96,
        };
        AutomationProperties.SetAutomationId(_box, "CommentBox");
        var save = new Button
        {
            Content = LanguageService.Get("comment.save"),
            IsDefault = true,
            MinWidth = 88,
        };
        AutomationProperties.SetAutomationId(save, "CommentSave");
        var cancel = new Button
        {
            Content = LanguageService.Get("common.cancel"),
            IsCancel = true,
            MinWidth = 88,
        };
        save.Click += (_, _) =>
        {
            _result = _box.Text ?? "";
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save },
        };
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children = { _box, buttons },
        };
    }

    public static async Task<string?> ShowAsync(Window? owner, string? current, int maxLength)
    {
        if (owner is null)
        {
            return null;
        }

        var window = new CommentPrompt(current, maxLength);
        await window.ShowDialog(owner);
        return window._result;
    }
}
