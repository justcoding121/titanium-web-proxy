using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Handles an inbound SOCKS4/SOCKS5 client on a <see cref="SocksProxyEndPoint" />.
    /// </summary>
    /// <param name="endPoint">The SOCKS endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    private async Task HandleClient(SocksProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        // Session registration happens in HandleClient(TransparentBase...) below once the SOCKS
        // handshake succeeds; early returns here never start a relay session.
        var cancellationToken = cancellationTokenSource.Token;

        var stream = clientConnection.GetStream();
        var buffer = BufferPool.GetBuffer();
        var port = 0;
        string? targetHost = null;
        try
        {
            if (!await TryReadExactAsync(stream, buffer, 0, 1, cancellationToken)) return;

            if (buffer[0] == 4)
            {
                // SOCKS4: no password auth. Reject when any validator is configured.
                if (HasSocksAuthenticator(endPoint)) return;

                // Remaining fixed header after VN: CD(1) DSTPORT(2) DSTIP(4).
                if (!await TryReadExactAsync(stream, buffer, 1, 7, cancellationToken)) return;
                if (buffer[1] != 1)
                    // not a CONNECT request
                    return;

                port = (buffer[2] << 8) + buffer[3];
                targetHost = new IPAddress(new[] { buffer[4], buffer[5], buffer[6], buffer[7] }).ToString();

                // Drain null-terminated userid (ignored; SOCKS4 has no password).
                while (true)
                {
                    if (!await TryReadExactAsync(stream, buffer, 8, 1, cancellationToken)) return;
                    if (buffer[8] == 0) break;
                }

                buffer[0] = 0;
                buffer[1] = 90; // request granted
                await stream.WriteAsync(buffer.AsMemory(0, 8), cancellationToken);
            }
            else if (buffer[0] == 5)
            {
                if (!await TryReadExactAsync(stream, buffer, 1, 1, cancellationToken)) return;
                int authenticationMethodCount = buffer[1];
                if (authenticationMethodCount < 1) return;
                if (!await TryReadExactAsync(stream, buffer, 2, authenticationMethodCount, cancellationToken))
                    return;

                var authConfigured = HasSocksAuthenticator(endPoint);
                var acceptedMethod = 255;
                for (var i = 0; i < authenticationMethodCount; i++)
                {
                    int method = buffer[i + 2];
                    if (method == 2 && authConfigured)
                    {
                        acceptedMethod = 2;
                        break;
                    }

                    if (method == 0 && !authConfigured)
                    {
                        acceptedMethod = 0;
                        break;
                    }
                }

                buffer[0] = 5;
                buffer[1] = (byte)acceptedMethod;
                await stream.WriteAsync(buffer.AsMemory(0, 2), cancellationToken);

                if (acceptedMethod == 255)
                    // no acceptable method
                    return;

                if (acceptedMethod == 2)
                {
                    // RFC 1929: VER(1) ULEN(1) UNAME PLEN(1) PASSWD — read framed fields.
                    if (!await TryReadExactAsync(stream, buffer, 0, 2, cancellationToken)) return;
                    if (buffer[0] != 1) return;

                    int userNameLength = buffer[1];
                    if (!await TryReadExactAsync(stream, buffer, 2, userNameLength + 1, cancellationToken))
                        return;

                    var userName = Encoding.ASCII.GetString(buffer, 2, userNameLength);
                    int passwordLength = buffer[2 + userNameLength];
                    if (!await TryReadExactAsync(stream, buffer, 3 + userNameLength, passwordLength,
                            cancellationToken))
                        return;

                    var password = Encoding.ASCII.GetString(buffer, 3 + userNameLength, passwordLength);
                    var success = await ValidateSocksCredentialsAsync(endPoint, clientConnection, userName,
                        password);

                    buffer[0] = 1;
                    buffer[1] = success ? (byte)0 : (byte)1;
                    await stream.WriteAsync(buffer.AsMemory(0, 2), cancellationToken);
                    if (!success) return;
                }

                // CONNECT request header: VER CMD RSV ATYP
                if (!await TryReadExactAsync(stream, buffer, 0, 4, cancellationToken)) return;
                if (buffer[0] != 5 || buffer[1] != 1) return;

                int addressLength;
                switch (buffer[3])
                {
                    case 1:
                        // IPv4
                        addressLength = 4;
                        if (!await TryReadExactAsync(stream, buffer, 4, addressLength + 2, cancellationToken))
                            return;
                        targetHost = new IPAddress(new[] { buffer[4], buffer[5], buffer[6], buffer[7] }).ToString();
                        port = (buffer[8] << 8) + buffer[9];
                        break;
                    case 3:
                        // Domain name
                        if (!await TryReadExactAsync(stream, buffer, 4, 1, cancellationToken)) return;
                        var nameLength = buffer[4];
                        addressLength = 1 + nameLength;
                        if (!await TryReadExactAsync(stream, buffer, 5, nameLength + 2, cancellationToken))
                            return;
                        targetHost = Encoding.ASCII.GetString(buffer, 5, nameLength);
                        port = (buffer[5 + nameLength] << 8) + buffer[6 + nameLength];
                        break;
                    case 4:
                        // IPv6
                        addressLength = 16;
                        if (!await TryReadExactAsync(stream, buffer, 4, addressLength + 2, cancellationToken))
                            return;
                        var ipv6Bytes = new byte[16];
                        Array.Copy(buffer, 4, ipv6Bytes, 0, 16);
                        targetHost = new IPAddress(ipv6Bytes).ToString();
                        port = (buffer[20] << 8) + buffer[21];
                        break;
                    default:
                        return;
                }

                // Echo request with REP=succeeded (bound address = request address).
                var replyLength = 4 + addressLength + 2;
                buffer[1] = 0;
                await stream.WriteAsync(buffer.AsMemory(0, replyLength), cancellationToken);
            }
            else
            {
                return;
            }
        }
        finally
        {
            BufferPool.ReturnBuffer(buffer);
        }

        await HandleClient(endPoint, clientConnection, port, cancellationTokenSource, cancellationToken,
            targetHost);
    }

    private bool HasSocksAuthenticator(SocksProxyEndPoint endPoint) =>
        endPoint.AuthenticateUserFunc != null || ProxyBasicAuthenticateFunc != null;

    /// <summary>
    ///     Precedence: endpoint <see cref="SocksProxyEndPoint.AuthenticateUserFunc" /> wins when set;
    ///     otherwise <see cref="ProxyBasicAuthenticateFunc" />. Username/password method is only
    ///     negotiated when at least one of these is configured.
    /// </summary>
    private async Task<bool> ValidateSocksCredentialsAsync(SocksProxyEndPoint endPoint,
        TcpClientConnection clientConnection, string userName, string password)
    {
        if (endPoint.AuthenticateUserFunc != null)
        {
            var args = new SocksAuthenticateEventArgs(this, clientConnection, endPoint, userName, password);
            return await endPoint.AuthenticateUserFunc(args);
        }

        if (ProxyBasicAuthenticateFunc != null)
            return await ProxyBasicAuthenticateFunc.Invoke(null, userName, password);

        // Should not reach here: method 2 is only selected when a validator is configured.
        return false;
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        var remaining = count;
        var position = offset;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(position, remaining), cancellationToken);
            if (read == 0) return false;
            position += read;
            remaining -= read;
        }

        return true;
    }
}
