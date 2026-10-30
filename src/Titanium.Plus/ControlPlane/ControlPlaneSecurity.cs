using System.Net;
using System.Security.Cryptography.X509Certificates;
using Titanium.Plus.Audit;
using Titanium.Plus.Security;

namespace Titanium.Plus.ControlPlane;

/// <summary>PEM certificate and key for the control plane and dashboard listeners.</summary>
public sealed record ControlPlaneTlsOptions(string CertificatePath, string KeyPath)
{
    /// <summary>
    ///     Loads the pair. Without <see cref="X509KeyStorageFlags.PersistKeySet"/> the key is removed from the
    ///     platform key store when the certificate is disposed, which the control plane does on shutdown.
    /// </summary>
    internal X509Certificate2 LoadCertificate()
    {
        if (string.IsNullOrWhiteSpace(CertificatePath) || string.IsNullOrWhiteSpace(KeyPath))
        {
            throw new InvalidOperationException("controlPlane.tls needs both certificatePath and keyPath.");
        }

        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);
            // A certificate built from PEM carries an ephemeral key that Windows' TLS stack will not use for a
            // server (EphemeralKeySet fails the handshake too); a PKCS#12 round trip gives it a key SslStream
            // accepts on every platform.
            return X509CertificateLoader.LoadPkcs12(
                pem.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.DefaultKeySet);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"controlPlane.tls certificate '{CertificatePath}' / key '{KeyPath}' could not be loaded ({ex.GetType().Name}: {ex.Message}).",
                ex);
        }
    }
}

/// <summary>
///     What the control plane and the dashboard share for access control: the audit trail and the failed-login
///     throttle, plus the one place that checks the shared secret. A lockout on one listener applies to the other.
/// </summary>
public sealed class ControlPlaneSecurity
{
    public const string ControlSurface = "control-plane";
    public const string DashboardSurface = "dashboard";

    public ControlPlaneSecurity(AuditLog? audit = null, AuthThrottle? throttle = null)
    {
        Audit = audit ?? new AuditLog();
        Throttle = throttle ?? new AuthThrottle();
        OwnsAudit = audit is null;
    }

    public AuditLog Audit { get; }

    public AuthThrottle Throttle { get; }

    /// <summary>True when this instance created the audit log and should dispose it.</summary>
    internal bool OwnsAudit { get; }

    /// <summary>
    ///     Returns null when the request carries the shared secret. Otherwise it returns the response to send (401, or
    ///     429 once the remote has used up its failures) and has already recorded <c>login-failed</c>.
    ///     <paramref name="actor"/> is a role label such as <c>dashboard:shared-secret</c>, never the secret.
    /// </summary>
    internal ControlHttpResponse? Authenticate(ControlHttpRequest request, string surface, string sharedSecret, out string actor)
    {
        var throttled = CheckThrottle(request, surface);
        if (throttled is not null)
        {
            actor = "anonymous";
            return throttled;
        }

        var presented = request.Header(ControlPlaneServer.SharedSecretHeader);
        if (!string.IsNullOrEmpty(presented) && ConstantTime.StringEquals(presented, sharedSecret))
        {
            actor = $"{surface}:shared-secret";
            return null;
        }

        actor = "anonymous";
        return RejectCredentials(request, surface);
    }

    /// <summary>The 429 for a remote that has used up its failures, or null. The first refusal of a lockout is audited.</summary>
    internal ControlHttpResponse? CheckThrottle(ControlHttpRequest request, string surface)
    {
        var remote = request.RemoteAddress?.ToString() ?? "unknown";
        if (!Throttle.IsBlocked(remote, out var retryAfter, out var first))
        {
            return null;
        }

        if (first)
        {
            Audit.Record($"{surface}:anonymous", remote, AuditActions.LoginFailed, request.Path, AuditResults.Throttled);
        }

        return ControlHttpResponse.Text(
            (int)HttpStatusCode.TooManyRequests,
            "text/plain; charset=utf-8",
            "too many failed attempts",
            new KeyValuePair<string, string>("Retry-After", retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Counts a failed credential against the remote, audits <c>login-failed</c>, and returns the 401.</summary>
    internal ControlHttpResponse RejectCredentials(ControlHttpRequest request, string surface)
    {
        var remote = request.RemoteAddress?.ToString() ?? "unknown";
        Throttle.RecordFailure(remote);
        Audit.Record($"{surface}:anonymous", remote, AuditActions.LoginFailed, request.Path, AuditResults.Denied);
        return ControlHttpResponse.Text((int)HttpStatusCode.Unauthorized, "text/plain; charset=utf-8", "unauthorized");
    }
}
