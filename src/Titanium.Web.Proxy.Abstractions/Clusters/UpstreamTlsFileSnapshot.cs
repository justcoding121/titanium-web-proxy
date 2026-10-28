using System.Security.Cryptography;
using System.Text;

namespace Titanium.Web.Proxy.Abstractions.Clusters;

/// <summary>
///     Raw bytes of the files a cluster's upstream TLS settings name, read once, plus a SHA-256 over them
///     and the non-file settings. The proxy parses these bytes, so the pool key and the loaded material
///     always describe the same content.
/// </summary>
internal sealed class UpstreamTlsFileSnapshot
{
    private UpstreamTlsFileSnapshot(byte[]? certificate, byte[]? key, byte[]? caBundle, string? error, string fingerprint)
    {
        Certificate = certificate;
        Key = key;
        CaBundle = caBundle;
        Error = error;
        Fingerprint = fingerprint;
    }

    internal byte[]? Certificate { get; }

    internal byte[]? Key { get; }

    internal byte[]? CaBundle { get; }

    /// <summary>First file that could not be read, or a pairing mistake. Null when every file was read.</summary>
    internal string? Error { get; }

    /// <summary>Lowercase hex SHA-256. Changes with file content, server name, and hostname policy.</summary>
    internal string Fingerprint { get; }

    internal static UpstreamTlsFileSnapshot Read(ClusterHttpClientConfig config)
    {
        string? error = null;
        var certificate = ReadFile(config.ClientCertificatePath, "clientCertificatePath", ref error);
        var key = ReadFile(config.ClientCertificateKeyPath, "clientCertificateKeyPath", ref error);
        var caBundle = ReadFile(config.CaBundlePath, "caBundlePath", ref error);
        if (error is null && config.ClientCertificateKeyPath is { Length: > 0 } &&
            string.IsNullOrEmpty(config.ClientCertificatePath))
        {
            error = "clientCertificateKeyPath requires clientCertificatePath.";
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "sn", config.ServerName);
        Append(hash, "vh", config.VerifyHostname ? "1" : "0");
        Append(hash, "pw", config.ClientCertificatePasswordEnv);
        Append(hash, "cert", certificate);
        Append(hash, "key", key);
        Append(hash, "ca", caBundle);
        Append(hash, "err", error);
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new UpstreamTlsFileSnapshot(certificate, key, caBundle, error, fingerprint);
    }

    private static byte[]? ReadFile(string? path, string setting, ref string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            error ??= $"{setting} '{path}' cannot be read: {ex.Message}";
            return null;
        }
    }

    private static void Append(IncrementalHash hash, string label, string? value) =>
        Append(hash, label, value is null ? null : Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, string label, byte[]? value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(label));
        hash.AppendData(BitConverter.GetBytes(value?.Length ?? -1));
        if (value is not null)
        {
            hash.AppendData(value);
        }
    }
}
