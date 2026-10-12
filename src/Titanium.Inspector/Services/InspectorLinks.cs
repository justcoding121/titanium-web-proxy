namespace Titanium.Inspector.Services;

/// <summary>Public pages About and the Help menu open. One definition so the addresses cannot drift.</summary>
public static class InspectorLinks
{
    public static Uri License { get; } = Create("https://", "polyformproject.org", "/licenses/noncommercial/1.0.0");

    public static Uri Website { get; } = Create("https://", "titaniumproxy.com", "");

    public static Uri Documentation { get; } = Create("https://", "titaniumproxy.com", "/docs/inspector");

    public static Uri ReportIssue { get; } = Create(
        "https://",
        "github.com",
        "/justcoding121/titanium-web-proxy/issues/new");

    private static Uri Create(string scheme, string host, string path) =>
        new(string.Concat(scheme, host, path));
}
