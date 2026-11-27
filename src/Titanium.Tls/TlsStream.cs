using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Tls;

/// <summary>A TLS 1.3 stream with the same shape as <see cref="SslStream"/>.</summary>
public sealed class TlsStream : Stream
{
    private const string AuthenticatedContext = "This operation is only allowed using a successfully authenticated context.";
    private const string ShutdownWrite = "Write operations are not allowed after the channel was shutdown.";

    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly TlsStreamOptions _options;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _encrypt = new byte[TlsRecordProtector.HeaderLength + TlsRecordProtector.MaxPlaintext + 1 + TlsRecordProtector.TagLength];
    private TlsEngine? _engine;
    private SslClientAuthenticationOptions? _clientOptions;
    private SslServerAuthenticationOptions? _serverOptions;
    private readonly byte[] _inbound = new byte[TlsRecordProtector.HeaderLength + TlsRecordProtector.MaxCiphertext];
    private int _inboundCount;
    private int _inboundUsed;
    private bool _authenticated;
    private bool _server;
    private bool _shutdown;
    private bool _readClosed;
    private bool _disposed;
    private bool _broken;

    /// <summary>Creates a stream that closes <paramref name="inner"/> on dispose.</summary>
    /// <param name="inner">The transport.</param>
    public TlsStream(Stream inner)
        : this(inner, false, null)
    {
    }

    /// <summary>Creates a stream and chooses whether dispose closes <paramref name="inner"/>.</summary>
    /// <param name="inner">The transport.</param>
    /// <param name="leaveInnerStreamOpen">True to leave <paramref name="inner"/> open.</param>
    public TlsStream(Stream inner, bool leaveInnerStreamOpen)
        : this(inner, leaveInnerStreamOpen, null)
    {
    }

    /// <summary>Creates a stream that closes <paramref name="inner"/> on dispose.</summary>
    /// <param name="inner">The transport.</param>
    /// <param name="options">Extra options. Null uses the defaults.</param>
    public TlsStream(Stream inner, TlsStreamOptions? options)
        : this(inner, false, options)
    {
    }

    /// <summary>Creates a stream.</summary>
    /// <param name="inner">The transport.</param>
    /// <param name="leaveInnerStreamOpen">True to leave <paramref name="inner"/> open.</param>
    /// <param name="options">Extra options. Null uses the defaults.</param>
    public TlsStream(Stream inner, bool leaveInnerStreamOpen, TlsStreamOptions? options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _leaveInnerOpen = leaveInnerStreamOpen;
        _options = options ?? new TlsStreamOptions();
    }

    /// <summary>True after a finished handshake.</summary>
    public bool IsAuthenticated => _authenticated && !_disposed;

    /// <summary>True after a finished handshake. The record layer is always encrypted.</summary>
    public bool IsEncrypted => IsAuthenticated;

    /// <summary>True after a finished handshake. TLS 1.3 authenticates the transcript.</summary>
    public bool IsSigned => IsAuthenticated;

    /// <summary>True when this side authenticated as the server.</summary>
    public bool IsServer => _server;

    /// <summary>True when a client certificate was authenticated.</summary>
    public bool IsMutuallyAuthenticated => _engine?.IsMutuallyAuthenticated == true;

    /// <summary>The negotiated protocol. This stack speaks TLS 1.3 only.</summary>
    public SslProtocols SslProtocol => _authenticated ? SslProtocols.Tls13 : SslProtocols.None;

    /// <summary>The negotiated cipher suite.</summary>
    public TlsCipherSuite NegotiatedCipherSuite => _authenticated ? (TlsCipherSuite)_engine!.SelectedCipher : TlsCipherSuite.TLS_AES_128_GCM_SHA256;

    /// <summary>The negotiated application protocol, or the empty protocol.</summary>
    public SslApplicationProtocol NegotiatedApplicationProtocol
    {
        get
        {
            string? protocol = _engine?.SelectedApplicationProtocol;
            return string.IsNullOrEmpty(protocol) ? default : new SslApplicationProtocol(protocol);
        }
    }

    /// <summary>The peer leaf, or null before the handshake produces one.</summary>
    public X509Certificate? RemoteCertificate => _engine?.RemoteCertificate;

    /// <summary>The local leaf used for authentication, or null.</summary>
    public X509Certificate? LocalCertificate => _engine?.LocalCertificate;

    /// <summary>The name passed to client authentication.</summary>
    public string TargetHostName => _clientOptions?.TargetHost ?? string.Empty;

    /// <summary>The fingerprint profile's fidelity, or the built-in hello's fidelity when no profile is set.</summary>
    public TlsFingerprintFidelity FingerprintFidelity => _options.FingerprintProfile?.Fidelity ?? TlsFingerprintFidelity.ReducedNoPostQuantum;

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_shutdown && _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Authenticates as the client.</summary>
    /// <param name="options">The client authentication options.</param>
    public void AuthenticateAsClient(SslClientAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LockBoth();
        try
        {
            PrepareClient(options);
            DriveSync();
        }
        finally
        {
            UnlockBoth();
        }
    }

    /// <summary>Authenticates as the client.</summary>
    /// <param name="options">The client authentication options.</param>
    /// <param name="cancellationToken">Cancels the transport reads and writes.</param>
    /// <returns>A task that completes when the handshake finishes.</returns>
    public Task AuthenticateAsClientAsync(SslClientAuthenticationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AuthenticateClientAsync(options, cancellationToken);
    }

    /// <summary>Authenticates as the server with one set of options.</summary>
    /// <param name="options">The server authentication options.</param>
    public void AuthenticateAsServer(SslServerAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LockBoth();
        try
        {
            PrepareServer(options);
            DriveSync();
        }
        finally
        {
            UnlockBoth();
        }
    }

    /// <summary>Authenticates as the server with one set of options.</summary>
    /// <param name="options">The server authentication options.</param>
    /// <returns>A task that completes when the handshake finishes.</returns>
    public Task AuthenticateAsServerAsync(SslServerAuthenticationOptions options)
    {
        return AuthenticateAsServerAsync(options, default);
    }

    /// <summary>Authenticates as the server with one set of options.</summary>
    /// <param name="options">The server authentication options.</param>
    /// <param name="cancellationToken">Cancels the transport reads and writes.</param>
    /// <returns>A task that completes when the handshake finishes.</returns>
    public Task AuthenticateAsServerAsync(SslServerAuthenticationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AuthenticateServerAsync(options, null, null, cancellationToken);
    }

    /// <summary>Authenticates as the server and selects options after the ClientHello.</summary>
    /// <param name="callback">The selection callback.</param>
    /// <param name="state">State passed to <paramref name="callback"/>.</param>
    /// <param name="cancellationToken">Cancels the transport reads and writes.</param>
    /// <returns>A task that completes when the handshake finishes.</returns>
    public Task AuthenticateAsServerAsync(TlsServerOptionsSelectionCallback callback, object? state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return AuthenticateServerAsync(null, callback, state, cancellationToken);
    }

    /// <summary>Sends <c>close_notify</c> and refuses further writes.</summary>
    /// <returns>A task that completes when the alert has been written.</returns>
    public async Task ShutdownAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureAuthenticated();
            if (_shutdown)
            {
                return;
            }

            int written = _engine!.ProtectAlert(1, 0, _encrypt);
            await _inner.WriteAsync(_encrypt.AsMemory(0, written)).ConfigureAwait(false);
            _shutdown = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Post-handshake client authentication is not supported.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>This method does not return.</returns>
    public Task NegotiateClientCertificateAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Post-handshake client authentication is not supported.");
    }

    /// <summary>Exports keying material from the TLS 1.3 exporter master secret.</summary>
    /// <param name="destination">The buffer that receives the material.</param>
    /// <param name="label">The exporter label.</param>
    /// <param name="context">The exporter context. Empty is allowed.</param>
    public void ExportKeyingMaterial(Span<byte> destination, string label, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(label);
        EnsureAuthenticated();
        _engine!.ExportKeyingMaterial(label, context, destination);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readLock.Wait();
        try
        {
            EnsureAuthenticated();
            if (buffer.Length == 0)
            {
                WaitForRecordSync();
                return 0;
            }

            while (_engine!.PlaintextAvailable == 0)
            {
                if (_readClosed)
                {
                    return 0;
                }

                PullSync();
            }

            return _engine.ReadPlaintext(buffer);
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAuthenticated();
            if (buffer.Length == 0)
            {
                await WaitForRecordAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            while (_engine!.PlaintextAvailable == 0)
            {
                if (_readClosed)
                {
                    return 0;
                }

                await PullAsync(cancellationToken).ConfigureAwait(false);
            }

            return _engine.ReadPlaintext(buffer.Span);
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            WriteLocked(buffer);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAuthenticated();
            if (_shutdown)
            {
                throw new InvalidOperationException(ShutdownWrite);
            }

            if (buffer.Length == 0)
            {
                return;
            }

            ReadOnlyMemory<byte> rest = buffer;
            while (rest.Length > 0)
            {
                await SendPendingKeyUpdateAsync(cancellationToken).ConfigureAwait(false);
                int count = rest.Length < TlsRecordProtector.MaxPlaintext ? rest.Length : TlsRecordProtector.MaxPlaintext;
                int written = _engine!.ProtectApplication(rest.Span[..count], _encrypt);
                await _inner.WriteAsync(_encrypt.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                rest = rest[count..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Flush();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _engine?.Dispose();
            _readLock.Dispose();
            _writeLock.Dispose();
            if (!_leaveInnerOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _engine?.Dispose();
            _readLock.Dispose();
            _writeLock.Dispose();
            if (!_leaveInnerOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    internal void UpdateKeys()
    {
        if (_engine is not null)
        {
            _engine.SendKeyUpdate = true;
        }
    }

    private async Task AuthenticateClientAsync(SslClientAuthenticationOptions options, CancellationToken cancellationToken)
    {
        await LockBothAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PrepareClient(options);
            await DriveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UnlockBoth();
        }
    }

    private async Task AuthenticateServerAsync(SslServerAuthenticationOptions? options, TlsServerOptionsSelectionCallback? callback, object? state, CancellationToken cancellationToken)
    {
        await LockBothAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PrepareServer(options ?? new SslServerAuthenticationOptions());
            _selectionCallback = callback;
            _selectionState = state;
            await DriveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _selectionCallback = null;
            UnlockBoth();
        }
    }

    private TlsServerOptionsSelectionCallback? _selectionCallback;
    private object? _selectionState;

    private void PrepareClient(SslClientAuthenticationOptions options)
    {
        if (_engine is not null)
        {
            throw new InvalidOperationException("The stream is already authenticated.");
        }

        RequireTls13(options.EnabledSslProtocols);
        if (options.CipherSuitesPolicy is not null && !AllowsAes128(options.CipherSuitesPolicy))
        {
            throw new AuthenticationException("The cipher suite policy does not include TLS_AES_128_GCM_SHA256.");
        }

        _clientOptions = options;
        _engine = new TlsEngine(TlsRole.Client, serverName: options.TargetHost, entropy: _options.Entropy, cache: _options.SessionCache)
        {
            TrustAnchors = _options.TrustAnchors,
            LocalCertificate = FirstCertificate(options.ClientCertificates),
            ApplicationProtocols = ProtocolNames(options.ApplicationProtocols),
            OfferStatusRequest = true,
        };
    }

    private void PrepareServer(SslServerAuthenticationOptions options)
    {
        if (_engine is not null)
        {
            throw new InvalidOperationException("The stream is already authenticated.");
        }

        RequireTls13(options.EnabledSslProtocols);
        _serverOptions = options;
        _server = true;
        _engine = new TlsEngine(TlsRole.Server, entropy: _options.Entropy, cache: _options.SessionCache);
    }

    private void DriveSync()
    {
        while (_engine!.State != TlsEngineState.Connected)
        {
            int consumed = Pump(WriteSync);
            if (_engine.State == TlsEngineState.Connected)
            {
                return;
            }

            if (consumed == 0 || _inboundUsed >= _inboundCount)
            {
                FillSync();
            }
        }
    }

    private async Task DriveAsync(CancellationToken cancellationToken)
    {
        while (_engine!.State != TlsEngineState.Connected)
        {
            int consumed = await PumpAsync(cancellationToken).ConfigureAwait(false);
            if (_engine.State == TlsEngineState.Connected)
            {
                return;
            }

            if (consumed == 0 || _inboundUsed >= _inboundCount)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private int Pump(Action<ReadOnlySpan<byte>> write)
    {
        var output = new TlsOutput();
        _engine!.Advance(Unconsumed(), out int consumed, output);
        _inboundUsed += consumed;
        if (output.Bytes.Length > 0)
        {
            write(output.Bytes);
        }

        Apply(output, write);
        FinishDrive();
        return consumed;
    }

    private async Task<int> PumpAsync(CancellationToken cancellationToken)
    {
        var output = new TlsOutput();
        _engine!.Advance(Unconsumed(), out int consumed, output);
        _inboundUsed += consumed;
        if (output.Bytes.Length > 0)
        {
            byte[] flight = output.Bytes.ToArray();
            await _inner.WriteAsync(flight, cancellationToken).ConfigureAwait(false);
        }

        await ApplyAsync(output, cancellationToken).ConfigureAwait(false);
        FinishDrive();
        return consumed;
    }

    private void FinishDrive()
    {
        if (_engine!.State == TlsEngineState.Connected)
        {
            if (!_authenticated)
            {
                _authenticated = true;
                if (_options.KeyLog is not null)
                {
                    _engine.WriteKeyLog(_options.KeyLog);
                }
            }

            return;
        }

        if (_engine.State == TlsEngineState.Closed)
        {
            throw new TlsAlertException(_engine.LastLocalAlert, sent: true);
        }
    }

    private ReadOnlySpan<byte> Unconsumed() => _inbound.AsSpan(_inboundUsed, _inboundCount - _inboundUsed);

    private void FillSync()
    {
        CompactInbound();
        if (_inboundCount == _inbound.Length)
        {
            throw new IOException("The TLS record is larger than the receive buffer.");
        }

        int read = _inner.Read(_inbound.AsSpan(_inboundCount));
        if (read == 0)
        {
            throw EndWithoutNotify();
        }

        _inboundCount += read;
    }

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        CompactInbound();
        if (_inboundCount == _inbound.Length)
        {
            throw new IOException("The TLS record is larger than the receive buffer.");
        }

        int read = await _inner.ReadAsync(_inbound.AsMemory(_inboundCount), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw EndWithoutNotify();
        }

        _inboundCount += read;
    }

    private void CompactInbound()
    {
        if (_inboundUsed == 0)
        {
            return;
        }

        int rest = _inboundCount - _inboundUsed;
        if (rest > 0)
        {
            Buffer.BlockCopy(_inbound, _inboundUsed, _inbound, 0, rest);
        }

        _inboundCount = rest;
        _inboundUsed = 0;
    }

    private void Apply(TlsOutput output, Action<ReadOnlySpan<byte>> write)
    {
        for (int i = 0; i < output.EventCount; i++)
        {
            TlsEvent ev = output.Event(i);
            if (ev.Kind == TlsEventKind.ServerCertificateReceived)
            {
                var resumed = new TlsOutput();
                try
                {
                    TlsCertificateDecision.ThrowIfRejected(_engine!.ServerCertificateErrors, _clientOptions?.RemoteCertificateValidationCallback, this, _engine.RemoteCertificate, _engine.ServerChain);
                    _engine.Resume(true, resumed);
                }
                catch (AuthenticationException)
                {
                    _engine!.Resume(false, resumed);
                    if (resumed.Bytes.Length > 0)
                    {
                        write(resumed.Bytes);
                    }

                    _engine.ReleaseServerCheck();
                    throw;
                }

                _engine.ReleaseServerCheck();
                if (resumed.Bytes.Length > 0)
                {
                    write(resumed.Bytes);
                }

                NoteCompleted(resumed);
            }
            else if (ev.Kind == TlsEventKind.ClientHelloReceived)
            {
                if (_selectionCallback is not null)
                {
                    throw new InvalidOperationException("The selection callback requires AuthenticateAsServerAsync.");
                }

                var resumed = new TlsOutput();
                _engine!.Resume(_ => BuildSelection(_serverOptions!), resumed);
                if (resumed.Bytes.Length > 0)
                {
                    write(resumed.Bytes);
                }

                NoteCompleted(resumed);
            }
            else if (ev.Kind == TlsEventKind.PeerAlert)
            {
                throw new TlsAlertException(ev.Alert, sent: false);
            }
        }
    }

    private async Task ApplyAsync(TlsOutput output, CancellationToken cancellationToken)
    {
        for (int i = 0; i < output.EventCount; i++)
        {
            TlsEvent ev = output.Event(i);
            if (ev.Kind == TlsEventKind.ServerCertificateReceived)
            {
                var resumed = new TlsOutput();
                try
                {
                    TlsCertificateDecision.ThrowIfRejected(_engine!.ServerCertificateErrors, _clientOptions?.RemoteCertificateValidationCallback, this, _engine.RemoteCertificate, _engine.ServerChain);
                    _engine.Resume(true, resumed);
                }
                catch (AuthenticationException)
                {
                    _engine!.Resume(false, resumed);
                    if (resumed.Bytes.Length > 0)
                    {
                        await _inner.WriteAsync(resumed.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                    }

                    _engine.ReleaseServerCheck();
                    throw;
                }

                _engine.ReleaseServerCheck();
                if (resumed.Bytes.Length > 0)
                {
                    await _inner.WriteAsync(resumed.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                NoteCompleted(resumed);
            }
            else if (ev.Kind == TlsEventKind.ClientHelloReceived)
            {
                if (_selectionCallback is not null)
                {
                    string name = _engine!.ClientHello?.ServerName ?? string.Empty;
                    SslServerAuthenticationOptions chosen = await _selectionCallback(this, new SslClientHelloInfo(name, SslProtocols.Tls13), _selectionState, cancellationToken).ConfigureAwait(false);
                    _serverOptions = chosen;
                }

                var resumed = new TlsOutput();
                _engine!.Resume(_ => BuildSelection(_serverOptions!), resumed);
                if (resumed.Bytes.Length > 0)
                {
                    await _inner.WriteAsync(resumed.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                NoteCompleted(resumed);
            }
            else if (ev.Kind == TlsEventKind.PeerAlert)
            {
                throw new TlsAlertException(ev.Alert, sent: false);
            }
        }
    }

    private void NoteCompleted(TlsOutput output)
    {
        for (int i = 0; i < output.EventCount; i++)
        {
            if (output.Event(i).Kind == TlsEventKind.HandshakeCompleted)
            {
                _authenticated = true;
                if (_options.KeyLog is not null)
                {
                    _engine!.WriteKeyLog(_options.KeyLog);
                }
            }
        }
    }

    private void PullSync()
    {
        ThrowIfBroken();
        while (_engine!.PlaintextAvailable == 0 && !_readClosed && _engine.State != TlsEngineState.Closed)
        {
            if (_inboundUsed >= _inboundCount)
            {
                FillSync();
            }

            var output = new TlsOutput();
            _engine.Advance(Unconsumed(), out int consumed, output);
            _inboundUsed += consumed;
            if (output.Bytes.Length > 0)
            {
                _inner.Write(output.Bytes);
            }

            NoteReadEvents(output);
            TrySendKeyUpdateFromReaderSync();
            if (consumed == 0 && _engine.PlaintextAvailable == 0 && !_readClosed)
            {
                FillSync();
            }
        }
    }

    private async Task PullAsync(CancellationToken cancellationToken)
    {
        ThrowIfBroken();
        while (_engine!.PlaintextAvailable == 0 && !_readClosed && _engine.State != TlsEngineState.Closed)
        {
            if (_inboundUsed >= _inboundCount)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }

            var output = new TlsOutput();
            _engine.Advance(Unconsumed(), out int consumed, output);
            _inboundUsed += consumed;
            if (output.Bytes.Length > 0)
            {
                await _inner.WriteAsync(output.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            NoteReadEvents(output);
            if (_engine.KeyUpdateResponseNeeded)
            {
                await TrySendKeyUpdateFromReaderAsync(cancellationToken).ConfigureAwait(false);
            }

            if (consumed == 0 && _engine.PlaintextAvailable == 0 && !_readClosed)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void WaitForRecordSync()
    {
        if (_engine!.PlaintextAvailable > 0 || _readClosed)
        {
            return;
        }

        PullSync();
    }

    private async Task WaitForRecordAsync(CancellationToken cancellationToken)
    {
        if (_engine!.PlaintextAvailable > 0 || _readClosed)
        {
            return;
        }

        await PullAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NoteReadEvents(TlsOutput output)
    {
        for (int i = 0; i < output.EventCount; i++)
        {
            TlsEvent ev = output.Event(i);
            if (ev.Kind == TlsEventKind.PeerAlert)
            {
                if (ev.Alert == 0)
                {
                    _readClosed = true;
                    return;
                }

                throw new IOException("The remote party sent TLS alert " + ev.Alert + ".", new TlsAlertException(ev.Alert, sent: false));
            }
        }

        if (_engine!.State == TlsEngineState.Closed && !_readClosed)
        {
            throw ClosedAlert(output);
        }
    }

    private void WriteLocked(ReadOnlySpan<byte> buffer)
    {
        EnsureAuthenticated();
        if (_shutdown)
        {
            throw new InvalidOperationException(ShutdownWrite);
        }

        if (buffer.Length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> rest = buffer;
        while (rest.Length > 0)
        {
            SendPendingKeyUpdateSync();
            int count = rest.Length < TlsRecordProtector.MaxPlaintext ? rest.Length : TlsRecordProtector.MaxPlaintext;
            int written = _engine!.ProtectApplication(rest[..count], _encrypt);
            _inner.Write(_encrypt.AsSpan(0, written));
            rest = rest[count..];
        }
    }

    private void SendPendingKeyUpdateSync()
    {
        if (_engine!.KeyUpdateResponseNeeded || _engine.WriteKeyUpdateDue || _engine.SendKeyUpdate)
        {
            byte request = _engine.KeyUpdateResponseNeeded ? (byte)0 : (byte)1;
            int written = _engine.ProtectKeyUpdate(request, _encrypt);
            _engine.SendKeyUpdate = false;
            _inner.Write(_encrypt.AsSpan(0, written));
        }
    }

    private async Task SendPendingKeyUpdateAsync(CancellationToken cancellationToken)
    {
        if (_engine!.KeyUpdateResponseNeeded || _engine.WriteKeyUpdateDue || _engine.SendKeyUpdate)
        {
            byte request = _engine.KeyUpdateResponseNeeded ? (byte)0 : (byte)1;
            int written = _engine.ProtectKeyUpdate(request, _encrypt);
            _engine.SendKeyUpdate = false;
            await _inner.WriteAsync(_encrypt.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
    }

    private void TrySendKeyUpdateFromReaderSync()
    {
        if (!_engine!.KeyUpdateResponseNeeded || !_writeLock.Wait(0))
        {
            return;
        }

        try
        {
            if (_engine.KeyUpdateResponseNeeded)
            {
                int written = _engine.ProtectKeyUpdate(0, _encrypt);
                _inner.Write(_encrypt.AsSpan(0, written));
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task TrySendKeyUpdateFromReaderAsync(CancellationToken cancellationToken)
    {
        if (!_writeLock.Wait(0, cancellationToken))
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            if (!_writeLock.Wait(0, cancellationToken))
            {
                return;
            }
        }

        try
        {
            if (_engine!.KeyUpdateResponseNeeded)
            {
                int written = _engine.ProtectKeyUpdate(0, _encrypt);
                await _inner.WriteAsync(_encrypt.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WriteSync(ReadOnlySpan<byte> bytes)
    {
        _inner.Write(bytes);
    }

    private static TlsServerSelection BuildSelection(SslServerAuthenticationOptions options)
    {
        X509Certificate2 certificate = CertificateOf(options) ?? throw new InvalidOperationException("A server certificate is required.");
        string[]? protocols = ProtocolNames(options.ApplicationProtocols);
        ushort[]? ciphers = CipherIds(options.CipherSuitesPolicy);
        return new TlsServerSelection
        {
            Certificate = certificate,
            ApplicationProtocols = protocols,
            RequireApplicationProtocol = protocols is { Length: > 0 },
            RequestClientCertificate = options.ClientCertificateRequired,
            CipherSuites = ciphers,
        };
    }

    private static X509Certificate2? CertificateOf(SslServerAuthenticationOptions options)
    {
        if (options.ServerCertificate is X509Certificate2 certificate)
        {
            return certificate;
        }

        return options.ServerCertificateContext?.TargetCertificate;
    }

    private static X509Certificate2? FirstCertificate(X509CertificateCollection? certificates)
    {
        if (certificates is null)
        {
            return null;
        }

        foreach (X509Certificate certificate in certificates)
        {
            if (certificate is X509Certificate2 current && current.HasPrivateKey)
            {
                return current;
            }
        }

        return null;
    }

    private static string[]? ProtocolNames(System.Collections.Generic.List<SslApplicationProtocol>? protocols)
    {
        if (protocols is null || protocols.Count == 0)
        {
            return null;
        }

        var names = new string[protocols.Count];
        for (int i = 0; i < protocols.Count; i++)
        {
            names[i] = System.Text.Encoding.ASCII.GetString(protocols[i].Protocol.Span);
        }

        return names;
    }

    private static ushort[]? CipherIds(CipherSuitesPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        var ids = new System.Collections.Generic.List<ushort>();
        try
        {
#pragma warning disable CA1416
            foreach (TlsCipherSuite suite in policy.AllowedCipherSuites)
            {
                ushort id = (ushort)suite;
                if (id is 0x1301 or 0x1302 or 0x1303)
                {
                    ids.Add(id);
                }
            }
#pragma warning restore CA1416
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }

        return ids.ToArray();
    }

    private static bool AllowsAes128(CipherSuitesPolicy policy)
    {
        try
        {
#pragma warning disable CA1416
            foreach (TlsCipherSuite suite in policy.AllowedCipherSuites)
            {
                if (suite == TlsCipherSuite.TLS_AES_128_GCM_SHA256)
                {
                    return true;
                }
            }
#pragma warning restore CA1416
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }

        return false;
    }

    private static void RequireTls13(SslProtocols protocols)
    {
        if (protocols != SslProtocols.None && (protocols & SslProtocols.Tls13) == 0)
        {
            throw new TlsVersionNotSupportedException();
        }
    }

    private void EnsureAuthenticated()
    {
        ThrowIfBroken();
        if (!_authenticated || _engine is null)
        {
            throw new InvalidOperationException(AuthenticatedContext);
        }
    }

    private void ThrowIfBroken()
    {
        if (_broken)
        {
            throw new IOException("The stream can no longer be used after a cancelled operation.");
        }
    }

    private IOException EndWithoutNotify()
    {
        _broken = true;
        return new IOException(_options.StrictCloseNotify
            ? "The remote party closed the connection without a close_notify alert."
            : "The remote party closed the connection without a close_notify alert.");
    }

    private IOException ClosedAlert(TlsOutput output)
    {
        for (int i = 0; i < output.EventCount; i++)
        {
            TlsEvent ev = output.Event(i);
            if (ev.Kind == TlsEventKind.PeerAlert)
            {
                if (ev.Alert == 0)
                {
                    _readClosed = true;
                    return new IOException("The remote party closed the connection.");
                }

                return new IOException("The remote party sent TLS alert " + ev.Alert + ".", new TlsAlertException(ev.Alert, sent: false));
            }
        }

        if (output.Bytes.Length > 0)
        {
            return new IOException("The handshake failed.", new TlsAlertException(output.Bytes[^1], sent: true));
        }

        return new IOException("The handshake failed.");
    }

    private void LockBoth()
    {
        _readLock.Wait();
        try
        {
            _writeLock.Wait();
        }
        catch
        {
            _readLock.Release();
            throw;
        }
    }

    private async Task LockBothAsync(CancellationToken cancellationToken)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _readLock.Release();
            throw;
        }
    }

    private void UnlockBoth()
    {
        _writeLock.Release();
        _readLock.Release();
    }
}
