using Microsoft.Extensions.Logging;
using Titanium.Tls;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private bool _useManagedTlsStack;

    private bool _useManagedQuicStack;

    private bool _allowManagedStackUnderFips;

    private static int _fipsOverrideWarned;

    /// <summary>
    ///     Gets or sets whether handshakes use the managed TLS stack.
    ///     The default is <see langword="false" />, so the proxy keeps <c>SslStream</c>.
    ///     Reading this after <see cref="Start" /> is allowed.
    ///     Assigning a different value after <see cref="Start" /> throws <see cref="System.InvalidOperationException" />.
    /// </summary>
    public bool UseManagedTlsStack
    {
        get => _useManagedTlsStack;
        set => AssignManagedStack(ref _useManagedTlsStack, value, nameof(UseManagedTlsStack));
    }

    /// <summary>
    ///     Gets or sets whether QUIC connections use the managed QUIC stack.
    ///     The default is <see langword="false" />, so the proxy keeps <c>System.Net.Quic</c>.
    ///     Reading this after <see cref="Start" /> is allowed.
    ///     Assigning a different value after <see cref="Start" /> throws <see cref="System.InvalidOperationException" />.
    /// </summary>
    public bool UseManagedQuicStack
    {
        get => _useManagedQuicStack;
        set => AssignManagedStack(ref _useManagedQuicStack, value, nameof(UseManagedQuicStack));
    }

    /// <summary>
    ///     Allows the managed stacks to start while FIPS mode is on.
    ///     The handshake then uses only FIPS-approved algorithms, so a browser fingerprint will not match,
    ///     and the managed stack is not a FIPS-validated module.
    ///     <c>TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1</c> is the same switch without this property.
    /// </summary>
    public bool AllowManagedStackUnderFips
    {
        get => _allowManagedStackUnderFips;
        set => AssignManagedStack(ref _allowManagedStackUnderFips, value, nameof(AllowManagedStackUnderFips));
    }

    private void AssignManagedStack(ref bool field, bool value, string name)
    {
        if (ProxyRunning && value != field)
        {
            throw new System.InvalidOperationException(name + " cannot be changed while the proxy is running.");
        }

        field = value;
    }

    internal TlsStream CreateManagedTlsStream(System.IO.Stream inner, bool leaveInnerStreamOpen) =>
        new(inner, leaveInnerStreamOpen, ManagedTlsOptions());

    internal TlsStreamOptions? ManagedTlsOptions() =>
        _allowManagedStackUnderFips ? new TlsStreamOptions { AllowManagedStackUnderFips = true } : null;

    private void EnsureManagedStackAllowedUnderFips()
    {
        string? refusal = FipsEnvironment.ManagedStackRefusal(
            _useManagedTlsStack || _useManagedQuicStack, _allowManagedStackUnderFips);
        if (refusal is not null)
        {
            throw new System.NotSupportedException(refusal);
        }

        if ((_useManagedTlsStack || _useManagedQuicStack) && FipsEnvironment.IsEnabled()
            && System.Threading.Interlocked.Exchange(ref _fipsOverrideWarned, 1) == 0)
        {
            logger.LogWarning(
                "FIPS mode is on and the managed stack override is set. Fingerprint fidelity is reduced and the managed stack is not FIPS-validated.");
        }
    }
}
