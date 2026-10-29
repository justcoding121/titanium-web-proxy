using System.Text;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Security.Oidc;

/// <summary>One <c>oidc</c> policy, read once: defaults applied, secrets loaded, lists made fast to test.</summary>
internal sealed class OidcSettings
{
    internal const string DefaultRedirectPath = "/_titanium/oidc/callback";
    internal const string DefaultCookieName = "titanium_oidc";
    internal const string DefaultGroupsClaim = "groups";
    internal const int DefaultSessionTtlSeconds = 28_800;

    /// <summary>The longest <c>name=value</c> pair a browser will keep, with room left for the attributes.</summary>
    internal const int MaxCookieBytes = 4096 - 160;

    private OidcSettings()
    {
    }

    public required string PolicyName { get; init; }
    public required string Issuer { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string RedirectPath { get; init; }
    public string? LogoutPath { get; init; }
    public Uri? PublicBase { get; init; }
    public required string[] Scopes { get; init; }
    public required string CookieName { get; init; }
    public required OidcCookieCrypto Crypto { get; init; }
    public TimeSpan SessionTtl { get; init; }
    public required HashSet<string> AllowedEmails { get; init; }
    public required HashSet<string> AllowedDomains { get; init; }
    public required HashSet<string> AllowedGroups { get; init; }
    public required string GroupsClaim { get; init; }
    public required KeyValuePair<string, string>[] PassClaims { get; init; }
    public required string[] RedirectOrigins { get; init; }
    public bool RefreshTokens { get; init; }

    public bool HasIdentityRules => AllowedEmails.Count > 0 || AllowedDomains.Count > 0;

    /// <summary>Prefix of the short-lived cookies that carry a login in progress.</summary>
    public string FlowCookiePrefix => CookieName + "_f";

    /// <summary>
    /// Throws <see cref="OidcConfigException"/> for anything the validator cannot see: an unset variable, a missing
    /// file, a key of the wrong size.
    /// </summary>
    public static OidcSettings Create(RoutePolicyConfig policy, Func<string, string?>? readEnv = null)
    {
        readEnv ??= Environment.GetEnvironmentVariable;
        var name = policy.Name;
        if (string.IsNullOrWhiteSpace(policy.Issuer) || string.IsNullOrWhiteSpace(policy.ClientId))
        {
            throw new OidcConfigException($"policy '{name}' needs issuer and clientId.");
        }

        var secret = ReadSecret(name, "clientSecret", policy.ClientSecretEnv, policy.ClientSecretFile, readEnv, required: false);
        var keyText = ReadSecret(name, "cookieSecret", policy.CookieSecretEnv, policy.CookieSecretFile, readEnv, required: true)!;
        OidcCookieCrypto crypto;
        try
        {
            crypto = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(keyText));
        }
        catch (OidcConfigException ex)
        {
            throw new OidcConfigException($"policy '{name}': {ex.Message}");
        }

        var refresh = policy.RefreshTokens == true;
        var scopes = BuildScopes(policy.Scopes, refresh);
        Uri? publicBase = null;
        if (!string.IsNullOrWhiteSpace(policy.PublicBaseUrl) &&
            Uri.TryCreate(policy.PublicBaseUrl, UriKind.Absolute, out var parsedBase))
        {
            publicBase = parsedBase;
        }

        return new OidcSettings
        {
            PolicyName = name,
            Issuer = policy.Issuer.Trim(),
            ClientId = policy.ClientId.Trim(),
            ClientSecret = secret,
            RedirectPath = string.IsNullOrWhiteSpace(policy.RedirectPath) ? DefaultRedirectPath : policy.RedirectPath.Trim(),
            LogoutPath = string.IsNullOrWhiteSpace(policy.LogoutPath) ? null : policy.LogoutPath.Trim(),
            PublicBase = publicBase,
            Scopes = scopes,
            CookieName = string.IsNullOrWhiteSpace(policy.CookieName) ? DefaultCookieName : policy.CookieName.Trim(),
            Crypto = crypto,
            SessionTtl = TimeSpan.FromSeconds(Math.Clamp(policy.SessionTtlSeconds ?? DefaultSessionTtlSeconds, 60, 2_592_000)),
            AllowedEmails = ToSet(policy.AllowedEmails, StringComparer.OrdinalIgnoreCase, lower: true),
            AllowedDomains = ToSet(policy.AllowedEmailDomains, StringComparer.OrdinalIgnoreCase, lower: true),
            AllowedGroups = ToSet(policy.AllowedGroups, StringComparer.Ordinal, lower: false),
            GroupsClaim = string.IsNullOrWhiteSpace(policy.GroupsClaim) ? DefaultGroupsClaim : policy.GroupsClaim.Trim(),
            PassClaims = (policy.PassClaims ?? new Dictionary<string, string>())
                .Where(static p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                .Select(static p => new KeyValuePair<string, string>(p.Key.Trim(), p.Value.Trim()))
                .ToArray(),
            RedirectOrigins = (policy.PostLoginRedirectAllowlist ?? [])
                .Where(static o => !string.IsNullOrWhiteSpace(o))
                .Select(static o => OidcRedirect.OriginOf(o.Trim()) ?? string.Empty)
                .Where(static o => o.Length > 0)
                .ToArray(),
            RefreshTokens = refresh,
        };
    }

    /// <summary>
    /// <c>(emails or domains) and groups</c>, each part only when configured. <paramref name="emailVerified"/> false
    /// never satisfies an e-mail rule. Null means the provider did not say.
    /// </summary>
    public bool Allows(string? email, bool? emailVerified, IReadOnlyCollection<string> groups)
    {
        if (HasIdentityRules)
        {
            if (string.IsNullOrEmpty(email) || emailVerified == false)
            {
                return false;
            }

            var at = email.LastIndexOf('@');
            var domain = at > 0 ? email[(at + 1)..] : string.Empty;
            if (!AllowedEmails.Contains(email) && !(domain.Length > 0 && AllowedDomains.Contains(domain)))
            {
                return false;
            }
        }

        if (AllowedGroups.Count > 0)
        {
            var member = false;
            foreach (var group in groups)
            {
                member |= AllowedGroups.Contains(group);
            }

            return member;
        }

        return true;
    }

    private static string[] BuildScopes(IReadOnlyList<string>? configured, bool refresh)
    {
        var scopes = new List<string>();
        if (configured is { Count: > 0 })
        {
            scopes.AddRange(configured.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()));
        }
        else
        {
            scopes.AddRange(["openid", "email", "profile"]);
            if (refresh)
            {
                scopes.Add("offline_access");
            }
        }

        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes.Insert(0, "openid");
        }

        return scopes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static HashSet<string> ToSet(IReadOnlyList<string>? values, StringComparer comparer, bool lower)
    {
        var set = new HashSet<string>(comparer);
        if (values is null)
        {
            return set;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var trimmed = value.Trim();
                set.Add(lower ? trimmed.ToLowerInvariant() : trimmed);
            }
        }

        return set;
    }

    private static string? ReadSecret(
        string policy, string label, string? env, string? file, Func<string, string?> readEnv, bool required)
    {
        if (!string.IsNullOrWhiteSpace(env))
        {
            var value = readEnv(env.Trim());
            if (string.IsNullOrEmpty(value))
            {
                throw new OidcConfigException($"policy '{policy}': {label}Env '{env.Trim()}' is not set or is empty.");
            }

            return value.TrimEnd('\r', '\n');
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            string text;
            try
            {
                text = File.ReadAllText(file.Trim(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OidcConfigException($"policy '{policy}': {label}File '{file.Trim()}' could not be read ({ex.GetType().Name}).");
            }

            text = text.TrimEnd('\r', '\n');
            if (text.Length == 0)
            {
                throw new OidcConfigException($"policy '{policy}': {label}File '{file.Trim()}' is empty.");
            }

            return text;
        }

        if (required)
        {
            throw new OidcConfigException($"policy '{policy}' needs {label}Env or {label}File.");
        }

        return null;
    }
}
