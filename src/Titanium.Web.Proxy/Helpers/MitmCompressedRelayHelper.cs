using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Detects append-only header mutations for MITM compressed HPACK/QPACK relay.
///     Up to <see cref="DefaultMaxAppendHeaders" /> new unique headers; remove/replace/non-unique edits
///     fall back to full re-encode.
/// </summary>
internal static class MitmCompressedRelayHelper
{
    /// <summary>Stack-friendly buffer for up to four dropped unique header names.</summary>
    internal struct DroppedNameBuffer
    {
        private string _n0, _n1, _n2, _n3;
        internal int Count { get; private set; }

        internal void Add(string name)
        {
            switch (Count++)
            {
                case 0: _n0 = name; break;
                case 1: _n1 = name; break;
                case 2: _n2 = name; break;
                default: _n3 = name; break;
            }
        }

        internal readonly string this[int index] => index switch
        {
            0 => _n0,
            1 => _n1,
            2 => _n2,
            3 => _n3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        internal readonly bool Contains(string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            for (var i = 0; i < Count; i++)
            {
                if (string.Equals(this[i], name, comparison))
                    return true;
            }

            return false;
        }
    }

    internal const int DefaultMaxAppendHeaders = 4;
    internal const int DefaultMaxDrops = 4;

    internal readonly struct AddedHeader
    {
        internal AddedHeader(string name, string value)
        {
            Name = name;
            Value = value;
        }

        internal string Name { get; }
        internal string Value { get; }
    }

    /// <summary>Stack-friendly buffer for up to four appended header literals.</summary>
    internal struct AddedHeaderBuffer
    {
        private AddedHeader _h0, _h1, _h2, _h3;
        internal int Count { get; private set; }

        internal void Add(string name, string value)
        {
            switch (Count++)
            {
                case 0: _h0 = new AddedHeader(name, value); break;
                case 1: _h1 = new AddedHeader(name, value); break;
                case 2: _h2 = new AddedHeader(name, value); break;
                default: _h3 = new AddedHeader(name, value); break;
            }
        }

        internal readonly AddedHeader this[int index] => index switch
        {
            0 => _h0,
            1 => _h1,
            2 => _h2,
            3 => _h3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        internal readonly bool ContainsName(string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            for (var i = 0; i < Count; i++)
            {
                if (string.Equals(this[i].Name, name, comparison))
                    return true;
            }

            return false;
        }
    }

    /// <summary>Unique-header snapshot at handler entry for append-only diff.</summary>
    internal readonly struct HeaderRelayBaseline
    {
        private readonly int _mutationCount;
        private readonly Dictionary<string, string> _unique;
        private readonly Dictionary<string, List<string>> _nonUniqueSnapshot;
        private readonly int _nonUniqueNamesAtCapture;

        internal HeaderRelayBaseline(int mutationCount, Dictionary<string, string> unique,
            Dictionary<string, List<string>> nonUniqueSnapshot, int nonUniqueNamesAtCapture)
        {
            _mutationCount = mutationCount;
            _unique = unique;
            _nonUniqueSnapshot = nonUniqueSnapshot;
            _nonUniqueNamesAtCapture = nonUniqueNamesAtCapture;
        }

        internal static HeaderRelayBaseline Capture(HeaderCollection headers)
        {
            var unique = new Dictionary<string, string>(headers.Headers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in headers.Headers)
                unique[kv.Key] = kv.Value.Value;

            var nonUnique = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in headers.NonUniqueHeaders)
            {
                var values = new List<string>(kv.Value.Count);
                foreach (var h in kv.Value)
                    values.Add(h.Value);
                nonUnique[kv.Key] = values;
            }

            return new HeaderRelayBaseline(headers.MutationCount, unique, nonUnique,
                headers.NonUniqueHeaders.Count);
        }

        internal int MutationCount => _mutationCount;

        internal bool TryDiffAppendOnly(HeaderCollection after, int maxAdds, out AddedHeaderBuffer added)
        {
            added = default;

            if (_nonUniqueNamesAtCapture > 0 || after.NonUniqueHeaders.Count > 0)
                return TryDiffNonUniqueTrailingAppend(after, maxAdds, out added);

            foreach (var kv in _unique)
            {
                if (!after.Headers.TryGetValue(kv.Key, out var header)
                    || !string.Equals(header.Value, kv.Value, StringComparison.Ordinal))
                    return false;
            }

            foreach (var kv in after.Headers)
            {
                if (_unique.ContainsKey(kv.Key))
                    continue;

                if (added.Count >= maxAdds)
                    return false;

                added.Add(kv.Key, kv.Value.Value);
            }

            if (added.Count == 0)
                return after.MutationCount == _mutationCount;

            return added.Count <= maxAdds;
        }

        /// <summary>Allow trailing values on existing non-unique header names (e.g. second Set-Cookie).</summary>
        private bool TryDiffNonUniqueTrailingAppend(HeaderCollection after, int maxAdds, out AddedHeaderBuffer added)
        {
            added = default;

            foreach (var kv in _unique)
            {
                if (after.NonUniqueHeaders.TryGetValue(kv.Key, out var grownList))
                {
                    if (grownList.Count < 1
                        || !string.Equals(kv.Value, grownList[0].Value, StringComparison.Ordinal))
                        return false;

                    for (var i = 1; i < grownList.Count; i++)
                    {
                        if (added.Count >= maxAdds)
                            return false;
                        added.Add(kv.Key, grownList[i].Value);
                    }

                    continue;
                }

                if (!after.Headers.TryGetValue(kv.Key, out var header)
                    || !string.Equals(header.Value, kv.Value, StringComparison.Ordinal))
                    return false;
            }

            foreach (var kv in after.Headers)
            {
                if (_unique.ContainsKey(kv.Key))
                    continue;

                if (added.Count >= maxAdds)
                    return false;

                added.Add(kv.Key, kv.Value.Value);
            }

            foreach (var kv in _nonUniqueSnapshot)
            {
                if (!after.NonUniqueHeaders.TryGetValue(kv.Key, out var afterList))
                    return false;

                var beforeList = kv.Value;
                if (afterList.Count < beforeList.Count)
                    return false;

                for (var i = 0; i < beforeList.Count; i++)
                {
                    if (!string.Equals(beforeList[i], afterList[i].Value, StringComparison.Ordinal))
                        return false;
                }

                for (var i = beforeList.Count; i < afterList.Count; i++)
                {
                    if (added.Count >= maxAdds)
                        return false;
                    added.Add(kv.Key, afterList[i].Value);
                }
            }

            foreach (var name in after.NonUniqueHeaders.Keys)
            {
                if (!_nonUniqueSnapshot.ContainsKey(name) && !_unique.ContainsKey(name))
                    return false;
            }

            if (added.Count == 0)
                return after.MutationCount == _mutationCount;

            return added.Count <= maxAdds;
        }

        /// <summary>Detect drop-only mutations (1..maxDrops unique keys removed, no adds/modifies).</summary>
        internal bool TryDiffDropOnly(HeaderCollection after, int maxDrops, out DroppedNameBuffer dropped)
        {
            dropped = default;

            if (_mutationCount == after.MutationCount)
                return false;

            if (_nonUniqueNamesAtCapture > 0 || after.NonUniqueHeaders.Count > 0)
                return false;

            var dropCount = 0;
            foreach (var kv in _unique)
            {
                if (after.Headers.TryGetValue(kv.Key, out var header))
                {
                    if (!string.Equals(header.Value, kv.Value, StringComparison.Ordinal))
                        return false;
                }
                else
                {
                    if (dropCount >= maxDrops)
                        return false;
                    dropped.Add(kv.Key);
                    dropCount++;
                }
            }

            if (dropCount == 0)
                return false;

            foreach (var kv in after.Headers)
            {
                if (!_unique.ContainsKey(kv.Key))
                    return false;
            }

            return dropCount <= maxDrops;
        }
    }

    internal static bool AllowsCompressedRelay(
        HeaderRelayBaseline baseline,
        HeaderCollection after,
        int maxAdds,
        out AddedHeaderBuffer added) =>
        baseline.TryDiffAppendOnly(after, maxAdds, out added);

    /// <summary>
    ///     MutationCount-only gate for unchanged headers. When counts diverge, caller must use
    ///     <see cref="HeaderRelayBaseline"/> snapshot diff.
    /// </summary>
    internal static bool AllowsCompressedRelay(
        int baselineMutationCount,
        HeaderCollection after,
        int maxAdds,
        out AddedHeaderBuffer added)
    {
        added = default;
        return after.MutationCount == baselineMutationCount;
    }
}
