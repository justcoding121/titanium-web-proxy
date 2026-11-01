namespace Titanium.Gateway.Kube;

/// <summary>Picks the newest served Gateway API version for one resource kind.</summary>
public static class ServedVersionPicker
{
    /// <summary>Known versions per plural resource name, newest first.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> KnownVersions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["tlsroutes"] = ["v1", "v1alpha3", "v1alpha2"],
        ["tcproutes"] = ["v1", "v1alpha2"],
        ["udproutes"] = ["v1", "v1alpha2"],
        ["grpcroutes"] = ["v1"],
        ["backendtlspolicies"] = ["v1", "v1alpha3", "v1beta1", "v1alpha2"],
    };

    /// <summary>Returns the newest known version present in <paramref name="served"/>, or null when none match.</summary>
    public static string? Pick(string pluralResourceName, IReadOnlyCollection<string> served)
    {
        if (!KnownVersions.TryGetValue(pluralResourceName, out var candidates))
        {
            return null;
        }

        var set = new HashSet<string>(served, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (set.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
