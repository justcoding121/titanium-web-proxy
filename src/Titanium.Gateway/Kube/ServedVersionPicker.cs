namespace Titanium.Gateway.Kube;

/// <summary>Picks the newest served Gateway API version for one resource kind.</summary>
public static class ServedVersionPicker
{
    private const string V1Alpha2 = "v1alpha2";

    /// <summary>Known versions per plural resource name, newest first.</summary>
    private static readonly Dictionary<string, string[]> KnownVersions = new(StringComparer.Ordinal)
    {
        ["tlsroutes"] = ["v1", "v1alpha3", V1Alpha2],
        ["tcproutes"] = ["v1", V1Alpha2],
        ["udproutes"] = ["v1", V1Alpha2],
        ["grpcroutes"] = ["v1"],
        ["backendtlspolicies"] = ["v1", "v1alpha3", "v1beta1", V1Alpha2],
    };

    /// <summary>Returns the newest known version present in <paramref name="served"/>, or null when none match.</summary>
    public static string? Pick(string pluralResourceName, IReadOnlyCollection<string> served)
    {
        if (!KnownVersions.TryGetValue(pluralResourceName, out var candidates))
        {
            return null;
        }

        var set = new HashSet<string>(served, StringComparer.Ordinal);
        return candidates.FirstOrDefault(set.Contains);
    }
}
