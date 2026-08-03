using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     RFC 9204 QPACK dynamic table.
///     Entries are identified by ever-increasing <b>absolute indices</b> (0-based, never wrap) so that
///     in-flight eviction protection is straightforward. Thread-safe via <see cref="ReaderWriterLockSlim" />:
///     reads (decode path) are far more common than writes (insert/evict).
/// </summary>
internal sealed class QpackDynamicTable : IDisposable
{
    // QPACK overhead per entry: 32 bytes (RFC 9204 §3.2.1, same as HPACK §4.1).
    private const int EntryOverhead = 32;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<(string Name, string Value)> _entries = new();

    // Absolute index of _entries[0].
    private ulong _baseAbsoluteIndex;

    /// <summary>Total number of entries ever inserted (never decremented on eviction).</summary>
    internal ulong InsertCount { get; private set; }

    /// <summary>Maximum total byte capacity of the table.</summary>
    internal uint Capacity { get; private set; }

    /// <summary>Current used bytes (name.Length + value.Length + 32 per entry).</summary>
    internal int Size { get; private set; }

    internal QpackDynamicTable(uint capacity)
    {
        Capacity = capacity;
    }

    /// <summary>
    ///     Inserts a new entry into the table, evicting oldest entries to make room (unless protected
    ///     by <paramref name="inFlightMinAbsoluteIndex" />). Returns the absolute index of the new entry.
    /// </summary>
    internal ulong Insert(string name, string value,
        ConcurrentDictionary<long, ulong>? inFlightMinAbsoluteIndex = null)
    {
        var entrySize = name.Length + value.Length + EntryOverhead;

        _lock.EnterWriteLock();
        try
        {
            // Evict oldest entries to make room, but skip any entry still referenced by an in-flight stream.
            while (_entries.Count > 0 && Size + entrySize > (int)Capacity)
            {
                var oldestAbsoluteIndex = _baseAbsoluteIndex;

                if (inFlightMinAbsoluteIndex != null)
                {
                    bool pinned = false;
                    foreach (var kvp in inFlightMinAbsoluteIndex)
                    {
                        if (oldestAbsoluteIndex >= kvp.Value)
                        {
                            pinned = true;
                            break;
                        }
                    }
                    if (pinned) break;
                }

                var removed = _entries[0];
                _entries.RemoveAt(0);
                Size -= removed.Name.Length + removed.Value.Length + EntryOverhead;
                _baseAbsoluteIndex++;
            }

            var absoluteIndex = InsertCount++;

            if (entrySize <= (int)Capacity &&
                (Size + entrySize <= (int)Capacity || _entries.Count == 0))
            {
                // If still not enough room after eviction (because of in-flight pins), skip storage but
                // still bump InsertCount so the RequiredInsertCount prefix stays consistent.
                _entries.Add((name, value));
                Size += entrySize;
            }

            return absoluteIndex;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Looks up an entry by absolute index. Returns false if evicted or out of range.</summary>
    internal bool TryGetByAbsoluteIndex(ulong absoluteIndex, out string name, out string value)
    {
        _lock.EnterReadLock();
        try
        {
            if (absoluteIndex < _baseAbsoluteIndex || absoluteIndex >= InsertCount)
            {
                name = string.Empty;
                value = string.Empty;
                return false;
            }

            var relIdx = (int)(absoluteIndex - _baseAbsoluteIndex);
            if (relIdx >= _entries.Count)
            {
                name = string.Empty;
                value = string.Empty;
                return false;
            }

            (name, value) = _entries[relIdx];
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    ///     Searches for the first entry matching <paramref name="name" /> (and optionally
    ///     <paramref name="value" />). Returns true when found; sets <paramref name="exactMatch" /> when
    ///     both name and value match.
    /// </summary>
    internal bool TryFind(string name, string value,
        out ulong absoluteIndex, out bool exactMatch)
    {
        _lock.EnterReadLock();
        try
        {
            ulong nameOnlyIdx = 0;
            bool foundName = false;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!string.Equals(e.Name, name, StringComparison.Ordinal)) continue;
                if (!foundName)
                {
                    nameOnlyIdx = _baseAbsoluteIndex + (ulong)i;
                    foundName = true;
                }
                if (string.Equals(e.Value, value, StringComparison.Ordinal))
                {
                    absoluteIndex = _baseAbsoluteIndex + (ulong)i;
                    exactMatch = true;
                    return true;
                }
            }

            absoluteIndex = foundName ? nameOnlyIdx : 0;
            exactMatch = false;
            return foundName;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Sets a new table capacity, evicting oldest entries until the size constraint is met.</summary>
    internal void SetCapacity(uint newCapacity)
    {
        _lock.EnterWriteLock();
        try
        {
            Capacity = newCapacity;
            while (_entries.Count > 0 && Size > (int)Capacity)
            {
                var removed = _entries[0];
                _entries.RemoveAt(0);
                Size -= removed.Name.Length + removed.Value.Length + EntryOverhead;
                _baseAbsoluteIndex++;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose() => _lock.Dispose();
}
