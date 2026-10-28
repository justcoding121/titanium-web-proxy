namespace Titanium.Web.Proxy.Configuration;

/// <summary>
///     Names of the sensitive <c>plus.options</c> keys that accept a secret reference. For each key <c>k</c>, the
///     config may carry <c>k</c> (a literal), <c>kEnv</c> (an environment variable name), or <c>kFile</c> (a file
///     path), at most one of the three.
/// </summary>
public static class SecretReferenceKeys
{
    /// <summary>Suffix of the sibling key that names an environment variable.</summary>
    public const string EnvSuffix = "Env";

    /// <summary>Suffix of the sibling key that names a file.</summary>
    public const string FileSuffix = "File";

    /// <summary>The <c>plus.options</c> keys that hold secrets.</summary>
    public static IReadOnlyList<string> PlusOptionKeys { get; } =
    [
        "security.apiKeys",
        "security.basicUsers",
        "state.redis",
        "discovery.consulToken",
    ];
}
