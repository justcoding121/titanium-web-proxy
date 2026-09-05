using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Cli.QaProbe;

public static class ConfigWriter
{
    public static string WriteForwardHost(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"fwd-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            """);
        return path;
    }

    public static string WriteRoutes(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"routes-{listenPort}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": {{listenPort}}, "decryptSsl": false }
              ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} }
                  ]
                }
              ]
            }
            """);
        return path;
    }

    public static string WriteStatic(string dir, int listenPort, string staticRoot)
    {
        var path = Path.Combine(dir, $"static-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
            staticFiles:
              root: "{staticRoot.Replace("\\", "/")}"
              enableGzip: true
            """);
        return path;
    }

    public static string WriteHttpServerConf(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"http-{listenPort}.conf");
        File.WriteAllText(path, $$"""
            listen {{listenPort}};
            server_name localhost;
            location / {
              proxy_pass http://127.0.0.1:{{originPort}};
            }
            """);
        return path;
    }

    public static string WriteSiteFileListenForward(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"site-{listenPort}.twp");
        // forward before listen so SiteFileReader applies pending ForwardHost on listen create.
        File.WriteAllText(path, $"forward 127.0.0.1:{originPort}\nlisten 127.0.0.1:{listenPort}\n");
        return path;
    }

    public static string WriteInvalid(string dir)
    {
        var path = Path.Combine(dir, "invalid.yaml");
        File.WriteAllText(path, "listeners:\n  - port: -1\n");
        return path;
    }

    public static string WriteLogging(string dir, int listenPort, int originPort, string logFile)
    {
        var path = Path.Combine(dir, $"log-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            logging:
              enabled: true
              minimumLevel: Information
              enableConsole: true
              enableFile: true
              filePath: "{logFile.Replace("\\", "/")}"
            """);
        return path;
    }

    public static string WriteListenerFlags(string dir, int listenPort, int originPort, bool enableHttp2, bool enableHttp3)
    {
        var path = Path.Combine(dir, $"flags-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: {enableHttp3.ToString().ToLowerInvariant()}
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
                enableHttp2: {enableHttp2.ToString().ToLowerInvariant()}
                enableHttp3: {enableHttp3.ToString().ToLowerInvariant()}
            """);
        return path;
    }

    public static string WriteTls(string dir, int listenPort, int originPort, string certPath, string keyPath)
    {
        var path = Path.Combine(dir, $"tls-{listenPort}.yaml");
        // Fixed GenericCertificate TLS-terminate requires proxy EnableHttp2=false (http/1.1-only ALPN).
        var keyLine = certPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                      certPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"""
                  privateKeyPath: "{keyPath.Replace("\\", "/")}"
            """;
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            server:
              enableHttp2: false
              enableHttp3: false
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: true
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
                enableHttp2: false
                enableHttp3: false
            certificates:
              certificatePath: "{certPath.Replace("\\", "/")}"
            {keyLine}
            """);
        return path;
    }

    public static string WriteExplicitMitm(string dir, int listenPort)
    {
        var path = Path.Combine(dir, $"mitm-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: true
            """);
        return path;
    }

    public static string WritePlus(string dir, int listenPort, int originPort, int controlPort, string secret)
    {
        var path = Path.Combine(dir, $"plus-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            plus:
              enabled: true
              controlPlane:
                host: "127.0.0.1"
                port: {controlPort}
                sharedSecret: "{secret}"
            """);
        return path;
    }

    public static (string CertPath, string KeyPath) WriteSelfSignedPem(string dir)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Prefer PFX for Windows Schannel server auth (PEM CreateFromPemFile often lacks usable private key flags).
        var pfxPath = Path.Combine(dir, "leaf.pfx");
        const string password = "cli-qa-probe";
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, password));
        Environment.SetEnvironmentVariable("TITANIUM_CERT_PASSWORD", password);

        // Also write PEM pair for operators / cross-check
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        File.WriteAllText(certPath, PemEncoding.WriteString("CERTIFICATE", cert.RawData));
        File.WriteAllText(keyPath, PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
        return (pfxPath, keyPath);
    }
}
