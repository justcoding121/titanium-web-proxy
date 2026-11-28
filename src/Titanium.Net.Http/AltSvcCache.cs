using System;
using System.Collections.Generic;

namespace Titanium.Net.Http;

/// <summary>
/// Remembers <c>Alt-Svc: h3</c> for one origin.
/// The cache holds 256 origins. An entry ends when its <c>ma</c> elapses.
/// A missing <c>ma</c> lasts 24 hours. <c>ma=0</c> and <c>clear</c> drop the entry.
/// A different host is ignored.
/// </summary>
internal sealed class AltSvcCache
{
    internal const int MaxEntries = 256;
    internal const int DefaultMaxAgeSeconds = 86400;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal bool TryGet(Uri uri, DateTimeOffset now, out int port)
    {
        port = 0;
        string key = Key(uri);
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out Entry entry))
            {
                return false;
            }

            if (entry.Expires <= now)
            {
                _entries.Remove(key);
                return false;
            }

            port = entry.Port;
            return true;
        }
    }

    internal void Note(Uri uri, string header, DateTimeOffset now)
    {
        string key = Key(uri);
        bool clear = false;
        int? port = null;
        int maxAge = DefaultMaxAgeSeconds;
        ReadOnlySpan<char> rest = header.AsSpan().Trim();
        while (!rest.IsEmpty)
        {
            int comma = IndexOfComma(rest);
            ReadOnlySpan<char> token = (comma >= 0 ? rest[..comma] : rest).Trim();
            rest = comma >= 0 ? rest[(comma + 1)..] : ReadOnlySpan<char>.Empty;
            if (token.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                clear = true;
                port = null;
                continue;
            }

            if (TryH3(token, uri.IdnHost, out int advertised, out int age))
            {
                clear = false;
                port = advertised;
                maxAge = age;
            }
        }

        lock (_entries)
        {
            if (port is int chosen && maxAge > 0)
            {
                if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
                {
                    DropExpired(now);
                }

                if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
                {
                    _entries.Clear();
                }

                _entries[key] = new Entry(chosen, now.AddSeconds(maxAge));
                return;
            }

            if (clear || maxAge <= 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private void DropExpired(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (KeyValuePair<string, Entry> entry in _entries)
        {
            if (entry.Value.Expires <= now)
            {
                stale ??= new List<string>();
                stale.Add(entry.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (string key in stale)
        {
            _entries.Remove(key);
        }
    }

    private static string Key(Uri uri)
    {
        int port = uri.IsDefaultPort ? 443 : uri.Port;
        return uri.Scheme + "://" + uri.IdnHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryH3(ReadOnlySpan<char> token, string originHost, out int port, out int maxAge)
    {
        port = 0;
        maxAge = DefaultMaxAgeSeconds;
        int eq = token.IndexOf('=');
        if (eq <= 0 || !token[..eq].Trim().Equals("h3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> rest = token[(eq + 1)..].Trim();
        if (!TryQuoted(ref rest, out ReadOnlySpan<char> authority) || !TryPort(authority, originHost, out port))
        {
            return false;
        }

        while (!rest.IsEmpty)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty || rest[0] != ';')
            {
                break;
            }

            rest = rest[1..].TrimStart();
            int end = rest.IndexOf(';');
            ReadOnlySpan<char> parameter = (end >= 0 ? rest[..end] : rest).Trim();
            rest = end >= 0 ? rest[end..] : ReadOnlySpan<char>.Empty;
            if (parameter.StartsWith("ma=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parameter[3..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int age))
            {
                maxAge = age;
            }
        }

        return true;
    }

    private static bool TryQuoted(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> value)
    {
        value = default;
        if (rest.IsEmpty || rest[0] != '"')
        {
            return false;
        }

        int close = rest[1..].IndexOf('"');
        if (close < 0)
        {
            return false;
        }

        value = rest[1..(close + 1)];
        rest = rest[(close + 2)..];
        return true;
    }

    private static bool TryPort(ReadOnlySpan<char> authority, string originHost, out int port)
    {
        port = 0;
        authority = authority.Trim();
        if (authority.IsEmpty)
        {
            return false;
        }

        if (authority[0] == ':')
        {
            return int.TryParse(authority[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
                && port is > 0 and <= 65535;
        }

        int colon = authority.LastIndexOf(':');
        ReadOnlySpan<char> host = colon >= 0 ? authority[..colon] : authority;
        if (host.Length > 0 && host[0] == '[')
        {
            int end = host.IndexOf(']');
            host = end > 1 ? host[1..end] : host;
        }

        if (host.Length > 0 && !host.Equals(originHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (colon < 0)
        {
            port = 443;
            return true;
        }

        return int.TryParse(authority[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }

    private static int IndexOfComma(ReadOnlySpan<char> value)
    {
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
            {
                quoted = !quoted;
            }
            else if (value[i] == ',' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct Entry(int Port, DateTimeOffset Expires);
}
