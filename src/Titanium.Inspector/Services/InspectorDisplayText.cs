using System.Globalization;
using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>
///     Bounds the text handed to the Inspect panel <c>TextBox</c>es. Avalonia lays the whole string
///     out on the UI thread, and the cost explodes for binary payloads decoded as UTF-8 (every
///     U+FFFD / control character needs font fallback): a 256 K-character git pack froze the window
///     for seconds when the Body tab was selected. This is display-only; captured bytes, Save body,
///     Hex and Copy are unaffected.
/// </summary>
public static class InspectorDisplayText
{
    /// <summary>Maximum characters shown for text-like content (about 0.1-0.3 s of layout).</summary>
    public const int MaxTextChars = 128 * 1024;

    /// <summary>Maximum characters shown for binary-looking content (unreadable beyond the head anyway).</summary>
    public const int MaxBinaryChars = 16 * 1024;

    private const int BinarySampleChars = 8 * 1024;
    private const int BinaryPercentThreshold = 10;

    /// <summary>
    ///     Returns <paramref name="text" /> unchanged (same instance) when it is short and renderable;
    ///     otherwise a copy with control / replacement characters shown as '.' and, past the cap,
    ///     truncated with a trailing note.
    /// </summary>
    public static string ForTextBox(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var binary = LooksBinary(text);
        var limit = binary ? MaxBinaryChars : MaxTextChars;
        var take = Math.Min(text.Length, limit);
        if (take < text.Length && char.IsHighSurrogate(text[take - 1]))
        {
            take--;
        }

        var truncated = take < text.Length;
        if (!truncated && !ContainsUnrenderable(text.AsSpan(0, take)))
        {
            return text;
        }

        var sb = new StringBuilder(take + 128);
        for (var i = 0; i < take; i++)
        {
            var c = text[i];
            sb.Append(IsUnrenderable(c) ? '.' : c);
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.Append("… display limited to the first ")
                .Append(take.ToString("N0", CultureInfo.CurrentCulture))
                .Append(" of ")
                .Append(text.Length.ToString("N0", CultureInfo.CurrentCulture))
                .Append(" characters");
            if (binary)
            {
                sb.Append(" (binary content, non-printable bytes shown as '.')");
            }

            sb.Append(" — use Save body or the Hex tab for the full content");
        }

        return sb.ToString();
    }

    /// <summary>True when a sample from the start of <paramref name="text" /> is mostly non-printable.</summary>
    internal static bool LooksBinary(string text)
    {
        var sample = Math.Min(text.Length, BinarySampleChars);
        var bad = 0;
        for (var i = 0; i < sample; i++)
        {
            if (IsUnrenderable(text[i]))
            {
                bad++;
            }
        }

        return bad * 100 >= sample * BinaryPercentThreshold && bad > 0;
    }

    private static bool ContainsUnrenderable(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (IsUnrenderable(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnrenderable(char c) =>
        c == '\uFFFD'
        || (c < ' ' && c != '\n' && c != '\r' && c != '\t')
        || (c >= '\u007F' && c <= '\u009F');
}
