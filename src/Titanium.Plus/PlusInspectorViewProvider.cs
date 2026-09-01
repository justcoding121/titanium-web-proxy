using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Plus Inspector panels — view provider only (Inspector never calls Apply).</summary>
public sealed class PlusInspectorViewProvider : IPlusInspectorViewProvider
{
    public Version RequiredAbstractionsVersion { get; } = new(7, 0, 1);

    public IReadOnlyList<object> CreatePanels(InspectorPanelContext context)
    {
        _ = context;
        return
        [
            new PlusInspectorPanel(
                "Plus Control Plane",
                "Cluster snapshot GET/PUT and destination drain (requires control secret).",
                "control-plane"),
            new PlusInspectorPanel(
                "Plus Observability",
                "Prometheus scrape endpoint on the dashboard port (/metrics).",
                "observability"),
            new PlusInspectorPanel(
                "Plus Dashboard",
                "Live destination table with drain/healthy actions.",
                "dashboard"),
        ];
    }
}

/// <summary>Typed panel descriptor consumed by Titanium Inspector.</summary>
public sealed class PlusInspectorPanel
{
    public PlusInspectorPanel(string title, string description, string id)
    {
        Title = title;
        Description = description;
        Id = id;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
}
