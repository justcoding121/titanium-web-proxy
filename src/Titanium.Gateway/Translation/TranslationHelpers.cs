using System.Text.RegularExpressions;
using Titanium.Gateway.Model;

namespace Titanium.Gateway.Translation;

/// <summary>Gateway API hostname rules: exact names and a single leading wildcard label.</summary>
internal static class Hostnames
{
    /// <summary>
    ///     Intersects two hostnames where null means "any". Returns false when no name can match both. The result is
    ///     the more specific of the two.
    /// </summary>
    public static bool TryIntersect(string? a, string? b, out string? result)
    {
        result = null;
        if (string.IsNullOrEmpty(a))
        {
            result = Normalize(b);
            return true;
        }

        if (string.IsNullOrEmpty(b))
        {
            result = Normalize(a);
            return true;
        }

        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b)
        {
            result = a;
            return true;
        }

        var aWild = a.StartsWith("*.", StringComparison.Ordinal);
        var bWild = b.StartsWith("*.", StringComparison.Ordinal);
        if (aWild && !bWild && b.EndsWith(a[1..], StringComparison.Ordinal) && b.Length > a.Length - 1)
        {
            result = b;
            return true;
        }

        if (bWild && !aWild && a.EndsWith(b[1..], StringComparison.Ordinal) && a.Length > b.Length - 1)
        {
            result = a;
            return true;
        }

        if (aWild && bWild)
        {
            if (a.EndsWith(b[1..], StringComparison.Ordinal))
            {
                result = a;
                return true;
            }

            if (b.EndsWith(a[1..], StringComparison.Ordinal))
            {
                result = b;
                return true;
            }
        }

        return false;
    }

    /// <summary>0 for an exact name, 1 for a wildcard, 2 for no hostname. Lower is more specific.</summary>
    public static int Rank(string? hostname) =>
        string.IsNullOrEmpty(hostname) ? 2 : hostname.StartsWith("*.", StringComparison.Ordinal) ? 1 : 0;

    public static string? Normalize(string? hostname) =>
        string.IsNullOrEmpty(hostname) || hostname == "*" ? null : hostname.ToLowerInvariant();
}

/// <summary>Parses a Gateway API <c>Duration</c> such as <c>500ms</c>, <c>10s</c>, or <c>1m30s</c>.</summary>
internal static partial class DurationParser
{
    [GeneratedRegex(@"^([0-9]{1,5}(h|m|s|ms)){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    [GeneratedRegex(@"([0-9]{1,5})(h|ms|m|s)", RegexOptions.CultureInvariant)]
    private static partial Regex Part();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text) || !Shape().IsMatch(text))
        {
            return false;
        }

        foreach (Match m in Part().Matches(text))
        {
            var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            value += m.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(n),
                "m" => TimeSpan.FromMinutes(n),
                "s" => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMilliseconds(n),
            };
        }

        return true;
    }
}

/// <summary>Builds the condition records the Gateway API specifies.</summary>
internal static class Conditions
{
    public const string Accepted = "Accepted";
    public const string Programmed = "Programmed";
    public const string ResolvedRefs = "ResolvedRefs";
    public const string Conflicted = "Conflicted";
    public const string PartiallyInvalid = "PartiallyInvalid";

    public static Condition Make(string type, bool status, string reason, string message, long generation) => new()
    {
        Type = type,
        Status = status ? "True" : "False",
        Reason = reason,
        Message = message,
        ObservedGeneration = generation,
    };
}

/// <summary>Resolves cross-namespace references against ReferenceGrants.</summary>
internal sealed class GrantIndex
{
    private readonly IReadOnlyList<ReferenceGrant> _grants;

    public GrantIndex(IReadOnlyList<ReferenceGrant> grants) => _grants = grants;

    /// <summary>
    ///     True when a ReferenceGrant in <paramref name="targetNamespace"/> lets <paramref name="fromKind"/> objects of
    ///     <paramref name="fromNamespace"/> reach the named target.
    /// </summary>
    public bool Allows(
        string fromGroup,
        string fromKind,
        string fromNamespace,
        string toGroup,
        string toKind,
        string targetNamespace,
        string targetName)
    {
        foreach (var grant in _grants)
        {
            if (!string.Equals(grant.Metadata.Namespace, targetNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            var fromOk = grant.Spec.From.Any(f =>
                f.Group == fromGroup && f.Kind == fromKind && f.Namespace == fromNamespace);
            if (!fromOk)
            {
                continue;
            }

            if (grant.Spec.To.Any(t =>
                    t.Group == toGroup && t.Kind == toKind && (string.IsNullOrEmpty(t.Name) || t.Name == targetName)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Evaluates a Kubernetes label selector over namespace labels.</summary>
internal static class Selectors
{
    public static bool Matches(LabelSelector? selector, IReadOnlyDictionary<string, string> labels)
    {
        if (selector is null)
        {
            return true;
        }

        if (selector.MatchLabels is not null)
        {
            foreach (var (key, value) in selector.MatchLabels)
            {
                if (!labels.TryGetValue(key, out var found) || found != value)
                {
                    return false;
                }
            }
        }

        if (selector.MatchExpressions is null)
        {
            return true;
        }

        foreach (var req in selector.MatchExpressions)
        {
            var has = labels.TryGetValue(req.Key, out var current);
            var ok = req.Operator switch
            {
                "In" => has && req.Values?.Contains(current!) == true,
                "NotIn" => !has || req.Values?.Contains(current!) != true,
                "Exists" => has,
                "DoesNotExist" => !has,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
