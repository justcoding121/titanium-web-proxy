using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Manager.Validation;

/// <summary>
/// Validates fleet revision YAML with <see cref="TwpConfigValidator"/> and rejects inline secret literals
/// (secrets must be supplied via *Env or *File references so values are not stored in the manager DB).
/// </summary>
internal static class ConfigRevisionValidator
{
    public static IReadOnlyList<string> ValidateYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        TwpConfig config;
        try
        {
            config = TwpConfigLoader.LoadYaml(yaml);
        }
        catch (Exception ex)
        {
            return [$"YAML parse error: {ex.Message}"];
        }

        try
        {
            var errors = new List<string>(TwpConfigValidator.Validate(config));
            RejectInlineSecrets(config, errors);
            return errors;
        }
        catch (Exception ex)
        {
            // Hostile / half-parsed YAML can leave null members the schema validator does not expect.
            return [$"YAML validate error: {ex.Message}"];
        }
    }

    private static void RejectInlineSecrets(TwpConfig config, List<string> errors)
    {
        if (config.Server?.Admin is { } admin && !string.IsNullOrEmpty(admin.AuthToken))
        {
            errors.Add("Inline secret rejected: server.admin.authToken (use authTokenEnv or authTokenFile).");
        }

        if (config.Plus is null)
        {
            return;
        }

        if (config.Plus.ControlPlane is { } cp)
        {
            RejectLiteral("plus.controlPlane.sharedSecret", cp.SharedSecret, errors);
            RejectLiteral("plus.controlPlane.dashboardViewerSecret", cp.DashboardViewerSecret, errors);
            if (cp.DashboardOidc is { } oidc)
            {
                RejectLiteral("plus.controlPlane.dashboardOidc.clientSecret", oidc.ClientSecret, errors);
                RejectLiteral("plus.controlPlane.dashboardOidc.cookieSecret", oidc.CookieSecret, errors);
            }
        }

        if (config.Plus.Options is not { Count: > 0 } options)
        {
            return;
        }

        foreach (var key in SecretReferenceKeys.PlusOptionKeys)
        {
            if (FindOption(options, key) is { Length: > 0 })
            {
                errors.Add(
                    $"Inline secret rejected: plus.options.{key} (use {key}{SecretReferenceKeys.EnvSuffix} or {key}{SecretReferenceKeys.FileSuffix}).");
            }
        }
    }

    private static void RejectLiteral(string path, string? literal, List<string> errors)
    {
        if (!string.IsNullOrEmpty(literal))
        {
            errors.Add($"Inline secret rejected: {path} (use {path}Env or {path}File).");
        }
    }

    private static string? FindOption(IReadOnlyDictionary<string, string> options, string key)
    {
        foreach (var (k, v) in options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
