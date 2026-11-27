using System;
using System.Collections.Generic;

namespace Titanium.Quic;

/// <summary>A debug event list. Nothing is recorded until <see cref="Enabled"/> is set.</summary>
internal sealed class QuicQlog
{
    internal bool Enabled { get; init; }

    internal List<string> Events { get; } = [];

    internal void Write(string name)
    {
        if (Enabled)
        {
            Events.Add(name);
        }
    }
}

/// <summary>In-process network. It can drop, reorder, duplicate, delay, corrupt, black-hole, and enforce an MTU.</summary>
internal sealed class QuicSimulator
{
    private readonly List<Item> _items = [];

    internal int Mtu { get; set; } = 65535;

    internal int DropCount { get; set; }

    internal bool Reorder { get; set; }

    internal bool Duplicate { get; set; }

    internal TimeSpan Delay { get; set; }

    internal int LossEvery { get; set; }

    internal int CorruptCount { get; set; }

    internal bool Blackhole { get; set; }

    internal QuicQlog? Qlog { get; set; }

    private int _lossCount;

    internal void Submit(bool toServer, ReadOnlySpan<byte> datagram, DateTimeOffset now)
    {
        if (datagram.IsEmpty || datagram.Length > Mtu)
        {
            Qlog?.Write("packet_dropped");
            return;
        }

        if (DropCount > 0)
        {
            DropCount--;
            Qlog?.Write("packet_dropped");
            return;
        }

        if (LossEvery > 0)
        {
            _lossCount++;
            if (_lossCount % LossEvery == 0)
            {
                Qlog?.Write("packet_dropped");
                return;
            }
        }

        if (Blackhole)
        {
            Qlog?.Write("packet_dropped");
            return;
        }

        byte[] copy = datagram.ToArray();
        if (CorruptCount > 0 && copy.Length > 0)
        {
            CorruptCount--;
            copy[^1] ^= 0x01;
        }

        Add(toServer, copy, now + Delay);
        if (Duplicate)
        {
            Add(toServer, (byte[])copy.Clone(), now + Delay);
        }
    }

    internal DateTimeOffset? NextDelivery()
    {
        DateTimeOffset? next = null;
        for (int i = 0; i < _items.Count; i++)
        {
            if (next is null || _items[i].At < next)
            {
                next = _items[i].At;
            }
        }

        return next;
    }

    internal void Deliver(QuicConnectionCore core, bool toServer, DateTimeOffset now, in PathInfo path)
    {
        for (int i = 0; i < _items.Count;)
        {
            Item item = _items[i];
            if (item.ToServer != toServer || item.At > now)
            {
                i++;
                continue;
            }

            _items.RemoveAt(i);
            Qlog?.Write("packet_received");
            core.HandleDatagram(now, item.Data, path);
        }
    }

    private void Add(bool toServer, byte[] data, DateTimeOffset at)
    {
        _items.Add(new Item(toServer, data, at));
        if (!Reorder || _items.Count < 2)
        {
            return;
        }

        Item newer = _items[^1];
        for (int i = _items.Count - 2; i >= 0; i--)
        {
            if (_items[i].ToServer != newer.ToServer)
            {
                continue;
            }

            Item older = _items[i];
            _items[i] = newer;
            _items[^1] = older;
            return;
        }
    }

    private readonly struct Item
    {
        internal Item(bool toServer, byte[] data, DateTimeOffset at)
        {
            ToServer = toServer;
            Data = data;
            At = at;
        }

        internal bool ToServer { get; }

        internal byte[] Data { get; }

        internal DateTimeOffset At { get; }
    }
}
