using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text;

namespace Titanium.Inspector.Views;

/// <summary>
/// Shared search field for the toolbar and the Advanced filters window.
/// Text wraps visually and stays one logical line: Enter does not insert a break,
/// and pasted line breaks become spaces.
/// </summary>
public class SearchQueryTextBox : TextBox
{
    private bool _collapsing;

    public SearchQueryTextBox()
    {
        TextWrapping = TextWrapping.Wrap;
        AcceptsReturn = false;
        VerticalContentAlignment = VerticalAlignment.Top;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Turns line breaks into single spaces. Other whitespace is left as typed.</summary>
    public static string CollapseLineBreaks(string text)
    {
        if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var breakRun = false;
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                breakRun = true;
                continue;
            }

            if (breakRun)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                breakRun = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_collapsing || change.Property != TextProperty || Text is not { } text)
        {
            return;
        }

        var collapsed = CollapseLineBreaks(text);
        if (ReferenceEquals(collapsed, text))
        {
            return;
        }

        _collapsing = true;
        try
        {
            var caret = CaretIndex;
            Text = collapsed;
            CaretIndex = Math.Clamp(caret, 0, collapsed.Length);
        }
        finally
        {
            _collapsing = false;
        }
    }
}
