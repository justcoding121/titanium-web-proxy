using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal sealed class TlsResumptionTicket
{
    internal TlsResumptionTicket(byte[] ticket, uint lifetimeSeconds, uint ageAdd, byte[] nonce, byte[] psk, ushort cipherSuite, long issuedUnixSeconds)
    {
        Ticket = ticket;
        LifetimeSeconds = lifetimeSeconds;
        AgeAdd = ageAdd;
        Nonce = nonce;
        Psk = psk;
        CipherSuite = cipherSuite;
        IssuedUnixSeconds = issuedUnixSeconds;
    }

    internal byte[] Ticket { get; }

    internal uint LifetimeSeconds { get; }

    internal uint AgeAdd { get; }

    internal byte[] Nonce { get; }

    internal byte[] Psk { get; }

    internal ushort CipherSuite { get; }

    internal long IssuedUnixSeconds { get; }

    internal void Clear()
    {
        CryptographicOperations.ZeroMemory(Psk);
    }
}

internal interface ITlsSessionCache
{
    bool TryGet(string serverName, string profileName, long nowUnixSeconds, out TlsResumptionTicket ticket);

    void Store(string serverName, string profileName, TlsResumptionTicket ticket);

    void Forget(string serverName, string profileName);
}

/// <summary>Bounded in-memory tickets, keyed by server name and profile name.</summary>
internal sealed class MemoryTlsSessionCache : ITlsSessionCache
{
    internal const int Capacity = 32;

    internal int QueuedKeys
    {
        get
        {
            lock (_gate) return _order.Count;
        }
    }

    private readonly Dictionary<string, TlsResumptionTicket> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public bool TryGet(string serverName, string profileName, long nowUnixSeconds, out TlsResumptionTicket ticket)
    {
        string key = Key(serverName, profileName);
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out ticket!))
            {
                return false;
            }

            if (ticket.LifetimeSeconds == 0 || nowUnixSeconds > ticket.IssuedUnixSeconds + ticket.LifetimeSeconds)
            {
                ticket.Clear();
                _items.Remove(key);
                return false;
            }

            return true;
        }
    }

    public void Store(string serverName, string profileName, TlsResumptionTicket ticket)
    {
        string key = Key(serverName, profileName);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out TlsResumptionTicket? previous))
            {
                previous.Clear();
            }
            else
            {
                _order.Enqueue(key);
            }

            _items[key] = ticket;
            while (_items.Count > Capacity && _order.TryDequeue(out string? old))
            {
                if (_items.Remove(old, out TlsResumptionTicket? evicted))
                {
                    evicted.Clear();
                }
            }
        }
    }

    public void Forget(string serverName, string profileName)
    {
        string key = Key(serverName, profileName);
        lock (_gate)
        {
            if (_items.Remove(key, out TlsResumptionTicket? evicted))
            {
                evicted.Clear();
            }
        }
    }

    private static string Key(string serverName, string profileName)
    {
        return serverName + "\n" + profileName;
    }
}
