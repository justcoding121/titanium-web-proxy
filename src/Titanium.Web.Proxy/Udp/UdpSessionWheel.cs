using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Udp;

/// <summary>
///     Hashed timer wheel for session idle expiry. A session is placed in the slot of its expected expiry once and
///     is never moved when traffic arrives: it only stamps <see cref="UdpSession.LastActivityMs" />. When the wheel
///     reaches the slot, a session that was active since is put back in the slot of its new expiry. This keeps the
///     per-datagram path free of timer work and uses one timer for every session.
/// </summary>
internal sealed class UdpSessionWheel
{
    private readonly long idleMs;
    private readonly long tickMs;
    private readonly List<UdpSession>[] slots;
    private readonly List<UdpSession> scratch = new();
    private long currentTick;

    public UdpSessionWheel(TimeSpan idleTimeout, long startMs)
    {
        idleMs = (long)idleTimeout.TotalMilliseconds;
        tickMs = Math.Clamp(idleMs / 8, 50, 1000);

        // Every expiry lies within idleMs of "now", and a slot holds one tick, so idle/tick + 2 slots never alias.
        slots = new List<UdpSession>[(idleMs / tickMs) + 3];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new List<UdpSession>();
        }

        currentTick = startMs / tickMs;
    }

    /// <summary>Milliseconds between wheel ticks. A session closes between idle and idle plus this long.</summary>
    public long TickMilliseconds => tickMs;

    public int Count { get; private set; }

    public void Add(UdpSession session)
    {
        Place(session, session.LastActivityMs + idleMs);
        Count++;
    }

    /// <summary>
    ///     Advances to <paramref name="nowMs" />. Appends sessions that had no traffic for the idle timeout to
    ///     <paramref name="expired" /> and forgets them; keeps the rest.
    /// </summary>
    public void Advance(long nowMs, List<UdpSession> expired)
    {
        var target = nowMs / tickMs;
        if (target <= currentTick)
        {
            return;
        }

        if (target - currentTick >= slots.Length)
        {
            // The clock jumped past a full revolution (a stalled process or a test). Every slot is due once.
            for (var i = 0; i < slots.Length; i++)
            {
                Drain(slots[i], nowMs, expired);
            }

            currentTick = target;
            return;
        }

        while (currentTick < target)
        {
            currentTick++;
            Drain(slots[(int)(currentTick % slots.Length)], nowMs, expired);
        }
    }

    private void Drain(List<UdpSession> slot, long nowMs, List<UdpSession> expired)
    {
        if (slot.Count == 0)
        {
            return;
        }

        scratch.Clear();
        scratch.AddRange(slot);
        slot.Clear();
        foreach (var session in scratch)
        {
            if (session.IsClosed)
            {
                Count--;
                continue;
            }

            var due = session.LastActivityMs + idleMs;
            if (due <= nowMs)
            {
                Count--;
                expired.Add(session);
            }
            else
            {
                Place(session, due);
            }
        }

        scratch.Clear();
    }

    private void Place(UdpSession session, long dueMs)
    {
        var tick = (dueMs + tickMs - 1) / tickMs;
        if (tick <= currentTick)
        {
            tick = currentTick + 1;
        }

        slots[(int)(tick % slots.Length)].Add(session);
    }
}
