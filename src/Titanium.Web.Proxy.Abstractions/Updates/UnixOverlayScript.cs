using System.Text;

namespace Titanium.Web.Proxy.Abstractions.Updates;

/// <summary>
/// Shared bash fragment for in-place zip updates. One copy so CLI and Inspector
/// do not duplicate the remove-then-copy loop.
/// </summary>
public static class UnixOverlayScript
{
    /// <summary>
    /// Copy files and symlinks from <paramref name="src"/> onto <paramref name="dest"/>
    /// without following links, and recreate empty directories. Paths that exist only
    /// in the destination are left in place. When <paramref name="stopOnError"/> is set,
    /// <c>cp</c> and <c>mkdir</c> failures call the script's <c>die</c> function.
    /// </summary>
    public static void AppendUnixRemoveThenCopy(StringBuilder sb, string src, string dest, bool stopOnError)
    {
        sb.AppendLine("while IFS= read -r -d '' f; do");
        sb.AppendLine($"  rel=\"${{f#\"{src}\"/}}\"");
        sb.AppendLine("  target=\"" + dest + "/$rel\"");
        sb.AppendLine("  mkdir -p \"$(dirname \"$target\")\"");
        sb.AppendLine("  rm -f \"$target\"");
        sb.AppendLine(stopOnError
            ? "  cp -Pp \"$f\" \"$target\" || die \"cp $target\""
            : "  cp -Pp \"$f\" \"$target\"");
        sb.AppendLine($"done < <(find \"{src}\" \\( -type f -o -type l \\) -print0)");
        sb.AppendLine("while IFS= read -r -d '' d; do");
        sb.AppendLine($"  rel=\"${{d#\"{src}\"/}}\"");
        sb.AppendLine("  if [[ -n \"$rel\" ]]; then");
        sb.AppendLine(stopOnError
            ? "    mkdir -p \"" + dest + "/$rel\" || die \"mkdir $rel\""
            : "    mkdir -p \"" + dest + "/$rel\"");
        sb.AppendLine("  fi");
        sb.AppendLine($"done < <(find \"{src}\" -mindepth 1 -type d -print0)");
    }
}
