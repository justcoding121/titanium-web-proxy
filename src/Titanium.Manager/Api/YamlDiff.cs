using System.Text;

namespace Titanium.Manager.Api;

internal static class YamlDiff
{
    public static string Unified(string fromText, string toText, string fromLabel = "from", string toLabel = "to")
    {
        var a = (fromText ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (toText ?? "").Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append("--- ").Append(fromLabel).Append('\n');
        sb.Append("+++ ").Append(toLabel).Append('\n');

        // Simple LCS-free line diff: mark removals/additions by index alignment with Myers-lite fallback.
        var i = 0;
        var j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j])
            {
                sb.Append(' ').Append(a[i]).Append('\n');
                i++;
                j++;
                continue;
            }

            if (j < b.Length && (i >= a.Length || !LineExistsAhead(a, i, b[j])))
            {
                sb.Append('+').Append(b[j]).Append('\n');
                j++;
                continue;
            }

            if (i < a.Length)
            {
                sb.Append('-').Append(a[i]).Append('\n');
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool LineExistsAhead(string[] lines, int start, string value)
    {
        for (var k = start; k < Math.Min(lines.Length, start + 32); k++)
        {
            if (lines[k] == value)
            {
                return true;
            }
        }

        return false;
    }
}
