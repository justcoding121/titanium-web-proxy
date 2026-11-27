using System.Buffers;

namespace Titanium.Tls;

internal sealed class TemplateFingerprintProfile : TlsFingerprintProfile
{
    private readonly ClientHelloTemplate _template;
    private readonly IEntropySource _entropy;

    internal TemplateFingerprintProfile(ClientHelloTemplate template, IEntropySource? entropy = null)
    {
        _template = template;
        _entropy = entropy ?? new SystemEntropy();
    }

    public override string Name => _template.Name;

    public override string Version => _template.Version;

    public override TlsFingerprintFidelity Fidelity => _template.Fidelity;

    public override void BuildClientHello(ClientHelloContext context, IBufferWriter<byte> destination)
    {
        ClientHelloBuilder.Write(_template, context, _entropy, destination);
    }
}
