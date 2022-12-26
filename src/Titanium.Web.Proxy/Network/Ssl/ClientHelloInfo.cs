using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.StreamExtended;

/// <summary>
///     Wraps up the client SSL hello information.
/// </summary>
public class ClientHelloInfo
{
    private static readonly string[] compressions =
    {
        "null",
        "DEFLATE"
    };

    internal ClientHelloInfo(int handshakeVersion, int majorVersion, int minorVersion, byte[] random, byte[] sessionId,
        int[] ciphers, int clientHelloLength)
    {
        HandshakeVersion = handshakeVersion;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        Random = random;
        SessionId = sessionId;
        Ciphers = ciphers;
        ClientHelloLength = clientHelloLength;
    }

    public int HandshakeVersion { get; }

    public int MajorVersion { get; }

    public int MinorVersion { get; }

    public byte[] Random { get; }

    public DateTime Time
    {
        get
        {
            var time = DateTime.MinValue;
            if (Random.Length > 3)
                time = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(((uint)Random[3] << 24) + ((uint)Random[2] << 16) + ((uint)Random[1] << 8) + Random[0])
                    .ToLocalTime();

            return time;
        }
    }

    public byte[] SessionId { get; }

    public int[] Ciphers { get; }

    public byte[]? CompressionData { get; internal set; }

    internal int ClientHelloLength { get; }

    internal int ExtensionsStartPosition { get; set; }

    public Dictionary<string, SslExtension>? Extensions { get; set; }

    public SslProtocols SslProtocol
    {
        get
        {
            var major = MajorVersion;
            var minor = MinorVersion;
            if (major == 3 && minor == 3)
            {
#if NET6_0_OR_GREATER
                var protocols = this.GetSslProtocols();
                if (protocols != null)
                {
                    if (protocols.Contains("Tls1.3"))
                    {
                        return SslProtocols.Tls12 | SslProtocols.Tls13;
                    }
                }
#endif

                return SslProtocols.Tls12;
            }

            if (major == 3 && minor == 2)
                return SslProtocols.Tls11;

            if (major == 3 && minor == 1)
                return SslProtocols.Tls;

#pragma warning disable 618
            if (major == 3 && minor == 0)
                return SslProtocols.Ssl3;

            if (major == 2 && minor == 0)
                return SslProtocols.Ssl2;
#pragma warning restore 618

            return SslProtocols.None;
        }
    }

    private static string SslVersionToString(int major, int minor)
    {
        var str = "Unknown";
        if (major == 3 && minor == 3)
            str = "TLS/1.2";
        else if (major == 3 && minor == 2)
            str = "TLS/1.1";
        else if (major == 3 && minor == 1)
            str = "TLS/1.0";
        else if (major == 3 && minor == 0)
            str = "SSL/3.0";
        else if (major == 2 && minor == 0)
            str = "SSL/2.0";

        return $"{major}.{minor} ({str})";
    }

    /// <summary>
    ///     Returns a <see cref="System.String" /> that represents this instance.
    /// </summary>
    /// <returns>
    ///     A <see cref="System.String" /> that represents this instance.
    /// </returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"A SSLv{HandshakeVersion}-compatible ClientHello handshake was found. Titanium extracted the parameters below.");
        sb.AppendLine();
        sb.AppendLine($"Version: {SslVersionToString(MajorVersion, MinorVersion)}");
        sb.AppendLine($"Random: {StringExtensions.ByteArrayToHexString(Random)}");
        sb.AppendLine($"\"Time\": {Time}");
        sb.AppendLine($"SessionID: {StringExtensions.ByteArrayToHexString(SessionId)}");

        if (Extensions != null)
        {
            sb.AppendLine("Extensions:");
            foreach (var extension in Extensions.Values.OrderBy(x => x.Position))
                sb.AppendLine($"{extension.Name}: {extension.Data}");
        }

        if (CompressionData != null && CompressionData.Length > 0)
        {
            int compressionMethod = CompressionData[0];
            var compression = compressions.Length > compressionMethod
                ? compressions[compressionMethod]
                : $"unknown [0x{compressionMethod:X2}]";
            sb.AppendLine($"Compression: {compression}");
        }

        if (Ciphers.Length > 0)
        {
            sb.AppendLine("Ciphers:");
            foreach (var cipherSuite in Ciphers)
            {
                if (!SslCiphers.Ciphers.TryGetValue(cipherSuite, out var cipherStr)) cipherStr = "unknown";

                sb.AppendLine($"[0x{cipherSuite:X4}] {cipherStr}");
            }
        }

        return sb.ToString();
    }
}