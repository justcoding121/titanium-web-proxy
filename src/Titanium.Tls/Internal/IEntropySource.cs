using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal interface IEntropySource
{
    void Fill(Span<byte> destination);
}

internal sealed class SystemEntropy : IEntropySource
{
    public void Fill(Span<byte> destination)
    {
        RandomNumberGenerator.Fill(destination);
    }
}
