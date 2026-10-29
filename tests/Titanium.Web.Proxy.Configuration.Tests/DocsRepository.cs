using System.Text;
using System.Text.RegularExpressions;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     Locates the repository and reads the Markdown the website and the GitHub wiki are built from, so a test can
///     check documentation against the schema and the validator.
/// </summary>
internal static partial class DocsRepository
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    public static string WebsiteDocs => Path.Combine(Root, "website", "docs");

    public static string Wiki => Path.Combine(Root, "wiki");

    public static string SchemaPath => Path.Combine(Root, "src", "Titanium.Web.Proxy.Configuration", "twp.schema.json");

    public static IEnumerable<string> WebsiteDocPages() =>
        Directory.EnumerateFiles(WebsiteDocs, "*.md").OrderBy(p => p, StringComparer.Ordinal);

    public static IEnumerable<string> WikiPages() =>
        Directory.EnumerateFiles(Wiki, "*.md").OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>
    ///     The heading ids VitePress renders for a page, including an explicit <c>{#id}</c>. Headings inside a fenced
    ///     code block are not headings.
    /// </summary>
    public static HashSet<string> WebsiteHeadingIds(string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in Headings(File.ReadAllLines(path)))
        {
            ids.Add(WebsiteSlug(heading));
        }

        return ids;
    }

    /// <summary>The text of every ATX heading outside a fenced code block.</summary>
    public static IEnumerable<string> Headings(IEnumerable<string> lines)
    {
        var inFence = false;
        foreach (var line in lines)
        {
            if (FenceRegex().IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = HeadingRegex().Match(line);
            if (match.Success)
            {
                yield return match.Groups[2].Value.Trim();
            }
        }
    }

    /// <summary>
    ///     VitePress slug: an explicit <c>{#id}</c> wins; otherwise inline markup is dropped, runs of whitespace and
    ///     punctuation become one hyphen, the ends are trimmed, and the result is lower case.
    /// </summary>
    public static string WebsiteSlug(string heading)
    {
        var explicitId = ExplicitIdRegex().Match(heading);
        if (explicitId.Success)
        {
            return explicitId.Groups[1].Value;
        }

        var text = LinkRegex().Replace(heading, "$1").Replace("`", string.Empty);
        var slug = SpecialRunRegex().Replace(text, "-");
        slug = HyphenRunRegex().Replace(slug, "-").Trim('-').ToLowerInvariant();
        if (slug.Length > 0 && char.IsDigit(slug[0]))
        {
            slug = "_" + slug;
        }

        return slug;
    }

    /// <summary>The first line of the file that is not blank, or an empty string.</summary>
    public static string FirstNonBlankLine(string path)
    {
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Trim().Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "website", "docs")) &&
                Directory.Exists(Path.Combine(dir.FullName, "wiki")) &&
                File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.Configuration", "twp.schema.json")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(#{1,6}) +(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\{#([^}\s]+)\}\s*$")]
    private static partial Regex ExplicitIdRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""[\s~`!@#$%^&*()\-_+=\[\]{}|\\;:"'“”‘’<>,.?/]+""")]
    private static partial Regex SpecialRunRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex HyphenRunRegex();
}
