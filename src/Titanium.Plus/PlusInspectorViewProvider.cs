using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Plus Inspector panels — view provider only (Inspector never calls Apply).</summary>
public sealed class PlusInspectorViewProvider : IPlusInspectorViewProvider
{
    public Version RequiredAbstractionsVersion { get; } = new(7, 0, 0);

    public IReadOnlyList<object> CreatePanels(InspectorPanelContext context)
    {
        _ = context;
        return
        [
            new { Title = "Plus Control Plane", Description = "Destination drain and cluster snapshot (NC)" },
            new { Title = "Plus Observability", Description = "Prometheus scrape endpoint status" },
        ];
    }
}
