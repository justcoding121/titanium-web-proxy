namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private bool _useManagedTlsStack;

    private bool _useManagedQuicStack;

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

    private void AssignManagedStack(ref bool field, bool value, string name)
    {
        if (ProxyRunning && value != field)
        {
            throw new System.InvalidOperationException(name + " cannot be changed while the proxy is running.");
        }

        field = value;
    }
}
