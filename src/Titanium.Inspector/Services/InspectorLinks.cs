namespace Titanium.Inspector.Services;

/// <summary>Public pages About and the Help menu open. One definition so the addresses cannot drift.</summary>
public static class InspectorLinks
{
    private const string HttpsScheme = "https://";

    public static Uri License { get; } = Create(HttpsScheme, "polyformproject.org", "/licenses/noncommercial/1.0.0");

    public static Uri Website { get; } = Create(HttpsScheme, "titaniumproxy.com", "");

    public static Uri Documentation { get; } = Create(HttpsScheme, "titaniumproxy.com", "/docs/inspector");

    public static Uri ReportIssue { get; } = Create(
        HttpsScheme,
        "github.com",
        "/justcoding121/titanium-web-proxy/issues/new");

    private static Uri Create(string scheme, string host, string path) =>
        new(string.Concat(scheme, host, path));
}
