using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>Fixed 48-byte pinned slabs for TLS secrets. A returned slab is wiped.</summary>
internal static class SecretBufferPool
{
    internal const int SlabLength = 48;

    private static readonly Stack<byte[]> Free = new();
    private static readonly object Gate = new();

    internal static byte[] Rent()
    {
        lock (Gate)
        {
            if (Free.Count > 0)
            {
                return Free.Pop();
            }
        }

        return GC.AllocateArray<byte>(SlabLength, pinned: true);
    }

    internal static void Return(byte[] slab)
    {
        CryptographicOperations.ZeroMemory(slab);
        lock (Gate)
        {
            Free.Push(slab);
        }
    }
}
