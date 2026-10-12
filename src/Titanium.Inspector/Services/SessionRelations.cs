namespace Titanium.Inspector.Services;

/// <summary>Ids chosen at click time. <see cref="Capped"/> means the list stopped at <see cref="MaxIds"/>.</summary>
public readonly record struct RelationResult(long[] Ids, bool Capped);

/// <summary>
/// Resolves "find related" sets. The redirect walk looks at the newest
/// <see cref="MaxRedirectScan"/> sessions and at most <see cref="MaxRedirectHops"/> hops.
/// </summary>
public static class SessionRelations
{
    public const int MaxIds = 500;
    public const int MaxRedirectHops = 20;
    public const int MaxRedirectScan = 5_000;

    public static RelationResult SameUrl(IReadOnlyList<SessionSnapshot> sessions, SessionSnapshot seed)
    {
        if (string.IsNullOrWhiteSpace(seed.Url))
        {
            return Empty;
        }

        return Pack(seed, sessions.Where(s =>
            s.Id != seed.Id &&
            s.Url.Equals(seed.Url, StringComparison.OrdinalIgnoreCase)));
    }

    public static RelationResult SameConnection(IReadOnlyList<SessionSnapshot> sessions, SessionSnapshot seed)
    {
        if (seed.ConnectionId <= 0)
        {
            return Empty;
        }

        return Pack(seed, sessions.Where(s => s.Id != seed.Id && s.ConnectionId == seed.ConnectionId));
    }

    public static RelationResult SameReferer(IReadOnlyList<SessionSnapshot> sessions, SessionSnapshot seed)
    {
        var referer = Header(seed.RequestHeadersText, "Referer");
        if (string.IsNullOrEmpty(referer))
        {
            return Empty;
        }

        return Pack(seed, sessions.Where(s =>
            s.Id != seed.Id &&
            string.Equals(Header(s.RequestHeadersText, "Referer"), referer, StringComparison.OrdinalIgnoreCase)));
    }

    public static RelationResult RedirectChain(IReadOnlyList<SessionSnapshot> sessions, SessionSnapshot seed)
    {
        var window = Newest(sessions, seed);
        var chain = new List<SessionSnapshot> { seed };
        var seen = new HashSet<long> { seed.Id };

        var current = seed;
        for (var hop = 0; hop < MaxRedirectHops; hop++)
        {
            var next = FindTarget(window, current, seen);
            if (next is null)
            {
                break;
            }

            chain.Add(next);
            seen.Add(next.Id);
            current = next;
        }

        current = seed;
        for (var hop = 0; hop < MaxRedirectHops; hop++)
        {
            var prev = FindSource(window, current, seen);
            if (prev is null)
            {
                break;
            }

            chain.Insert(0, prev);
            seen.Add(prev.Id);
            current = prev;
        }

        if (chain.Count <= 1)
        {
            return Empty;
        }

        return Pack(seed, chain.Where(s => s.Id != seed.Id));
    }

    private static readonly RelationResult Empty = new([], false);

    private static RelationResult Pack(SessionSnapshot seed, IEnumerable<SessionSnapshot> others)
    {
        var ids = others
            .Where(s => s.Id != seed.Id)
            .Select(s => s.Id)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return Empty;
        }

        ids.Add(seed.Id);
        ids.Sort();
        var capped = ids.Count > MaxIds;
        if (capped)
        {
            ids.RemoveRange(MaxIds, ids.Count - MaxIds);
        }

        return new RelationResult(ids.ToArray(), capped);
    }

    private static List<SessionSnapshot> Newest(IReadOnlyList<SessionSnapshot> sessions, SessionSnapshot seed)
    {
        if (sessions.Count <= MaxRedirectScan)
        {
            return sessions as List<SessionSnapshot> ?? sessions.ToList();
        }

        var window = sessions.OrderByDescending(s => s.Id).Take(MaxRedirectScan).ToList();
        if (window.TrueForAll(s => s.Id != seed.Id))
        {
            window.Add(seed);
        }

        return window;
    }

    private static SessionSnapshot? FindTarget(
        List<SessionSnapshot> window,
        SessionSnapshot current,
        HashSet<long> seen)
    {
        var location = Header(current.ResponseHeadersText, "Location");
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        var target = Resolve(current.Url, location);
        return window.FirstOrDefault(s => !seen.Contains(s.Id) && SameTarget(s.Url, target));
    }

    private static SessionSnapshot? FindSource(
        List<SessionSnapshot> window,
        SessionSnapshot current,
        HashSet<long> seen)
    {
        foreach (var session in window)
        {
            if (seen.Contains(session.Id))
            {
                continue;
            }

            var location = Header(session.ResponseHeadersText, "Location");
            if (string.IsNullOrEmpty(location))
            {
                continue;
            }

            if (SameTarget(Resolve(session.Url, location), current.Url))
            {
                return session;
            }
        }

        return null;
    }

    internal static string? Header(string? block, string name)
    {
        if (string.IsNullOrEmpty(block))
        {
            return null;
        }

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            if (line[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(idx + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string Resolve(string baseUrl, string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var basis) &&
            Uri.TryCreate(basis, location, out var combined))
        {
            return combined.AbsoluteUri;
        }

        return location;
    }

    private static bool SameTarget(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        if (Uri.TryCreate(left, UriKind.Absolute, out var a) &&
            Uri.TryCreate(right, UriKind.Absolute, out var b))
        {
            return string.Equals(a.AbsoluteUri, b.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
