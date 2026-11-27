using System;
using Titanium.Tls;

namespace Titanium.Tls.Tests.Support;

internal sealed class FixedEntropy : IEntropySource
{
    private readonly byte[] _script;
    private int _offset;

    internal FixedEntropy(byte[] script)
    {
        _script = script;
    }

    public void Fill(Span<byte> destination)
    {
        if (_offset + destination.Length > _script.Length)
        {
            throw new InvalidOperationException("The scripted entropy is exhausted.");
        }

        _script.AsSpan(_offset, destination.Length).CopyTo(destination);
        _offset += destination.Length;
    }
}
