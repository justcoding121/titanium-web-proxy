namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>xDS ADS client settings. Loaded only when this section is present.</summary>
public sealed class XdsConfig
{
    public string? Server { get; set; }

    public XdsTlsConfig? Tls { get; set; }

    public XdsNodeConfig? Node { get; set; }

    /// <summary>Subset of ADS types to subscribe to. Null means all supported types.</summary>
    public IList<string>? ResourceTypes { get; set; }

    public int? InitialFetchTimeoutSeconds { get; set; }

    /// <summary>When true, start with file routes/clusters if the control plane is unreachable.</summary>
    public bool FailOpen { get; set; } = true;
}

public sealed class XdsTlsConfig
{
    public string? CaPath { get; set; }

    public string? ClientCertPath { get; set; }

    public string? ClientKeyPath { get; set; }
}

public sealed class XdsNodeConfig
{
    public string? Id { get; set; }

    public string? Cluster { get; set; }

    public XdsLocalityConfig? Locality { get; set; }
}

public sealed class XdsLocalityConfig
{
    public string? Zone { get; set; }
}
