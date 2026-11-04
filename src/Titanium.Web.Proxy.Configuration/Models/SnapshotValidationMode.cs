namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>How strictly the Plus control plane validates snapshot PUT bodies.</summary>
public enum SnapshotValidationMode
{
    Off,
    Warn,
    Enforce,
}
