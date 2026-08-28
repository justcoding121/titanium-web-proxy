using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>Root Titanium Web Proxy configuration document (native twp.yaml / twp.json).</summary>
public sealed class TwpConfig
{
    public string? SchemaVersion { get; set; } = "7.0";

    public IList<ListenerConfig> Listeners { get; set; } = new List<ListenerConfig>();

    public IList<RouteConfig> Routes { get; set; } = new List<RouteConfig>();

    public IList<ClusterConfig> Clusters { get; set; } = new List<ClusterConfig>();

    public StaticFilesConfig? StaticFiles { get; set; }

    public PlusConfig? Plus { get; set; }

    public CertificatesConfig? Certificates { get; set; }
}

/// <summary>Listen endpoint binding.</summary>
public sealed class ListenerConfig
{
    public string? Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8000;

    public bool DecryptSsl { get; set; }

    /// <summary>When set, enables classic ForwardHost reverse without a route table.</summary>
    public string? ForwardHost { get; set; }

    public int? ForwardPort { get; set; }
}

/// <summary>Static file serving options.</summary>
public sealed class StaticFilesConfig
{
    public string? Root { get; set; }

    public bool EnableGzip { get; set; } = true;

    public bool EnableBrotli { get; set; }
}

/// <summary>Optional Plus feature switches (DLL still required beside the host).</summary>
public sealed class PlusConfig
{
    public bool Enabled { get; set; }

    public ControlPlaneConfig? ControlPlane { get; set; }

    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>Control-plane listen settings.</summary>
public sealed class ControlPlaneConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9080;

    public string? SharedSecret { get; set; }
}

/// <summary>Certificate / ACME related paths.</summary>
public sealed class CertificatesConfig
{
    public string? CertificatePath { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? AcmeEmail { get; set; }

    public string? AcmeDomain { get; set; }
}
