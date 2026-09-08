using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>Offline compare of two captured sessions (headers + bodies). No hot path.</summary>
public static class SessionDiff
{
    public static SessionDiffResult Compare(SessionSnapshot left, SessionSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var sb = new StringBuilder();
        var changes = 0;

        sb.AppendLine("=== Session Diff ===");
        sb.Append("A: #").Append(left.Id).Append(' ').Append(left.Method).Append(' ').AppendLine(left.Url);
        sb.Append("   status=").Append(FormatStatus(left.StatusCode)).AppendLine();
        sb.Append("B: #").Append(right.Id).Append(' ').Append(right.Method).Append(' ').AppendLine(right.Url);
        sb.Append("   status=").Append(FormatStatus(right.StatusCode)).AppendLine();
        sb.AppendLine();

        if (!string.Equals(left.Method, right.Method, StringComparison.OrdinalIgnoreCase))
        {
            changes++;
            sb.AppendLine($"Method: {left.Method} → {right.Method}");
        }

        if (!string.Equals(left.Url, right.Url, StringComparison.Ordinal))
        {
            changes++;
            sb.AppendLine($"URL: {left.Url}");
            sb.AppendLine($"  → {right.Url}");
        }

        if (left.StatusCode != right.StatusCode)
        {
            changes++;
            sb.AppendLine($"Status: {FormatStatus(left.StatusCode)} → {FormatStatus(right.StatusCode)}");
        }

        changes += AppendHeaderDiff(sb, "Request headers", left.RequestHeadersText, right.RequestHeadersText);
        changes += AppendHeaderDiff(sb, "Response headers", left.ResponseHeadersText, right.ResponseHeadersText);
        changes += AppendBodyDiff(sb, "Request body", ResolveBody(left, request: true), ResolveBody(right, request: true));
        changes += AppendBodyDiff(sb, "Response body", ResolveBody(left, request: false), ResolveBody(right, request: false));

        if (changes == 0)
        {
            sb.AppendLine("= (identical)");
        }
        else
        {
            sb.AppendLine();
            sb.Append(changes).Append(changes == 1 ? " difference." : " differences.");
        }

        return new SessionDiffResult(changes > 0, sb.ToString().TrimEnd());
    }

    private static string FormatStatus(int? code) => code?.ToString() ?? "(none)";

    private static string ResolveBody(SessionSnapshot snap, bool request)
    {
        if (request)
        {
            if (!string.IsNullOrEmpty(snap.RequestBodyText))
            {
                return snap.RequestBodyText;
            }

            if (snap.RequestBodyBytes is { Length: > 0 })
            {
                return Encoding.UTF8.GetString(snap.RequestBodyBytes);
            }

            return "";
        }

        if (!string.IsNullOrEmpty(snap.ResponseBodyText))
        {
            return snap.ResponseBodyText;
        }

        if (snap.ResponseBodyBytes is { Length: > 0 })
        {
            return Encoding.UTF8.GetString(snap.ResponseBodyBytes);
        }

        return "";
    }

    private static int AppendHeaderDiff(StringBuilder sb, string title, string? leftText, string? rightText)
    {
        var left = SessionInspectors.ParseHeaderBlock(leftText);
        var right = SessionInspectors.ParseHeaderBlock(rightText);
        var names = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var section = new StringBuilder();
        var count = 0;
        foreach (var name in names)
        {
            left.TryGetValue(name, out var lv);
            right.TryGetValue(name, out var rv);
            lv ??= "";
            rv ??= "";
            if (string.Equals(lv, rv, StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            if (string.IsNullOrEmpty(lv))
            {
                section.Append("+ ").Append(name).Append(": ").AppendLine(rv);
            }
            else if (string.IsNullOrEmpty(rv))
            {
                section.Append("- ").Append(name).Append(": ").AppendLine(lv);
            }
            else
            {
                section.Append("- ").Append(name).Append(": ").AppendLine(lv);
                section.Append("+ ").Append(name).Append(": ").AppendLine(rv);
            }
        }

        if (count == 0)
        {
            return 0;
        }

        sb.Append("--- ").Append(title).AppendLine(" ---");
        sb.Append(section);
        sb.AppendLine();
        return count;
    }

    private static int AppendBodyDiff(StringBuilder sb, string title, string left, string right) // NOSONAR S3776 -- Offline line diff; splitting would hide the bounded-lookahead contract.
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        sb.Append("--- ").Append(title).AppendLine(" ---");
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);

        if (leftLines.Count == 1 && rightLines.Count == 1
            && leftLines[0].Length < 200 && rightLines[0].Length < 200)
        {
            sb.Append("- ").AppendLine(leftLines[0]);
            sb.Append("+ ").AppendLine(rightLines[0]);
            sb.AppendLine();
            return 1;
        }

        var i = 0;
        var j = 0;
        var changes = 0;
        while (i < leftLines.Count && j < rightLines.Count)
        {
            if (string.Equals(leftLines[i], rightLines[j], StringComparison.Ordinal))
            {
                sb.Append("  ").AppendLine(leftLines[i]);
                i++;
                j++;
                continue;
            }

            var later = IndexOf(rightLines, leftLines[i], j + 1);
            if (later >= 0 && later - j <= 8)
            {
                while (j < later)
                {
                    sb.Append("+ ").AppendLine(rightLines[j++]);
                    changes++;
                }

                continue;
            }

            var laterLeft = IndexOf(leftLines, rightLines[j], i + 1);
            if (laterLeft >= 0 && laterLeft - i <= 8)
            {
                while (i < laterLeft)
                {
                    sb.Append("- ").AppendLine(leftLines[i++]);
                    changes++;
                }

                continue;
            }

            sb.Append("- ").AppendLine(leftLines[i++]);
            sb.Append("+ ").AppendLine(rightLines[j++]);
            changes += 2;
        }

        while (i < leftLines.Count)
        {
            sb.Append("- ").AppendLine(leftLines[i++]);
            changes++;
        }

        while (j < rightLines.Count)
        {
            sb.Append("+ ").AppendLine(rightLines[j++]);
            changes++;
        }

        if (changes == 0)
        {
            changes = 1;
            sb.AppendLine("(content differs)");
        }

        sb.AppendLine();
        return Math.Max(1, changes);
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
    }

    private static int IndexOf(IReadOnlyList<string> lines, string value, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (string.Equals(lines[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <param name="HasDifferences">True when any metadata, header, or body difference was found.</param>
/// <param name="Text">Human-readable report.</param>
public readonly record struct SessionDiffResult(bool HasDifferences, string Text);
