using System.Text;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Config;

/// <summary>
///     Turns secret references into values once, right after the config is validated. Each sensitive setting may
///     be a literal, <c>*Env</c> (an environment variable name), or <c>*File</c> (a file path, for example a mounted
///     Kubernetes Secret). Nothing else in the config is interpolated.
///     <para>
///         The validator has already ensured at most one form is set. Error text names the setting and the
///         variable or path, never a value. Every resolved value is registered with <see cref="SecretRedactor" />.
///     </para>
/// </summary>
internal static class SecretResolver
{
    /// <summary>Settings whose file form may hold one entry per line (joined with commas).</summary>
    private static readonly string[] ListKeys = ["security.apiKeys", "security.basicUsers"];

    /// <summary>Resolves in place and returns one error per reference that could not be read.</summary>
    public static IReadOnlyList<string> Resolve(TwpConfig config, Func<string, string?>? readEnv = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        readEnv ??= Environment.GetEnvironmentVariable;
        var errors = new List<string>();

        if (config.Plus?.ControlPlane is { } controlPlane)
        {
            controlPlane.SharedSecret = ResolveOne(
                "plus.controlPlane.sharedSecret",
                controlPlane.SharedSecret,
                controlPlane.SharedSecretEnv,
                controlPlane.SharedSecretFile,
                multiline: false,
                readEnv,
                errors);
            controlPlane.SharedSecretEnv = null;
            controlPlane.SharedSecretFile = null;
            controlPlane.DashboardViewerSecret = ResolveOne(
                "plus.controlPlane.dashboardViewerSecret",
                controlPlane.DashboardViewerSecret,
                controlPlane.DashboardViewerSecretEnv,
                controlPlane.DashboardViewerSecretFile,
                multiline: false,
                readEnv,
                errors);
            controlPlane.DashboardViewerSecretEnv = null;
            controlPlane.DashboardViewerSecretFile = null;
        }

        if (config.Plus?.Options is { Count: > 0 } options)
        {
            foreach (var key in SecretReferenceKeys.PlusOptionKeys)
            {
                ResolveOption(options, key, readEnv, errors);
            }
        }

        if (config.Server?.Admin is { Enabled: true } admin)
        {
            admin.AuthToken = ResolveOne(
                "server.admin.authToken",
                admin.AuthToken,
                admin.AuthTokenEnv,
                admin.AuthTokenFile,
                multiline: false,
                readEnv,
                errors);
        }

        return errors;
    }

    /// <summary>Every file a config reads for a secret, so a Secret rotation can trigger a reload.</summary>
    public static IReadOnlyList<string> SecretFiles(TwpConfig config)
    {
        var files = new List<string>();
        Add(files, config.Plus?.ControlPlane?.SharedSecretFile);
        Add(files, config.Plus?.ControlPlane?.DashboardViewerSecretFile);
        Add(files, config.Server?.Admin?.AuthTokenFile);
        if (config.Plus?.Options is { } options)
        {
            foreach (var key in SecretReferenceKeys.PlusOptionKeys)
            {
                Add(files, Find(options, key + SecretReferenceKeys.FileSuffix).Value);
            }
        }

        return files;

        static void Add(List<string> list, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(Path.GetFullPath(path));
            }
        }
    }

    private static void ResolveOption(
        Dictionary<string, string> options,
        string key,
        Func<string, string?> readEnv,
        List<string> errors)
    {
        var literal = Find(options, key);
        var env = Find(options, key + SecretReferenceKeys.EnvSuffix);
        var file = Find(options, key + SecretReferenceKeys.FileSuffix);
        if (env.Key is null && file.Key is null)
        {
            if (literal.Value is { Length: > 0 } plain)
            {
                SecretRedactor.Register(plain);
            }

            return;
        }

        var multiline = Array.IndexOf(ListKeys, key) >= 0;
        var value = ResolveOne($"plus.options.{key}", literal.Value, env.Value, file.Value, multiline, readEnv, errors);
        if (literal.Key is not null)
        {
            options.Remove(literal.Key);
        }

        if (env.Key is not null)
        {
            options.Remove(env.Key);
        }

        if (file.Key is not null)
        {
            options.Remove(file.Key);
        }

        if (!string.IsNullOrEmpty(value))
        {
            options[key] = value;
        }
    }

    private static string? ResolveOne(
        string path,
        string? literal,
        string? env,
        string? file,
        bool multiline,
        Func<string, string?> readEnv,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(env))
        {
            var name = env.Trim();
            var value = readEnv(name);
            if (string.IsNullOrEmpty(value))
            {
                errors.Add($"{path}{SecretReferenceKeys.EnvSuffix} '{name}' is not set or is empty.");
                return null;
            }

            return Accept(Normalize(value, multiline), multiline);
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
                errors.Add($"{path}{SecretReferenceKeys.FileSuffix} '{file.Trim()}' could not be read ({ex.GetType().Name}).");
                return null;
            }

            var normalized = Normalize(text, multiline);
            if (normalized.Length == 0)
            {
                errors.Add($"{path}{SecretReferenceKeys.FileSuffix} '{file.Trim()}' is empty.");
                return null;
            }

            return Accept(normalized, multiline);
        }

        return string.IsNullOrEmpty(literal) ? literal : Accept(literal, multiline);
    }

    /// <summary>
    ///     Strips the trailing newline that <c>echo</c> and editors add. A list setting may hold one entry per line;
    ///     those lines become the comma-separated form the setting already uses.
    /// </summary>
    private static string Normalize(string raw, bool multiline)
    {
        if (!multiline)
        {
            return raw.TrimEnd('\r', '\n');
        }

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(',', lines);
    }

    private static string Accept(string value, bool multiline = false)
    {
        SecretRedactor.Register(value);
        if (multiline)
        {
            // A list holds several secrets (and user:password pairs). Each piece is redacted on its own.
            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                SecretRedactor.Register(entry);
                var colon = entry.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0 && colon + 1 < entry.Length)
                {
                    SecretRedactor.Register(entry[(colon + 1)..]);
                }
            }
        }

        return value;
    }

    private static KeyValuePair<string?, string?> Find(Dictionary<string, string> options, string key)
    {
        foreach (var (name, value) in options)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return new KeyValuePair<string?, string?>(name, value);
            }
        }

        return default;
    }
}
