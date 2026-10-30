using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Control;

/// <summary>Where the controller publishes the data-plane config.</summary>
public sealed record OutputOptions
{
    public required string Namespace { get; init; }

    public required string ConfigMapName { get; init; }

    /// <summary>Key inside the ConfigMap. The data plane runs <c>titanium run --config /etc/titanium/twp.yaml</c>.</summary>
    public string ConfigKey { get; init; } = "twp.yaml";

    public required string TlsSecretName { get; init; }

    public string FieldManager { get; init; } = "titanium-gateway";

    /// <summary>
    ///     The data-plane Deployment. Listeners are bound at start and do not hot-reload, so when the set of
    ///     listeners changes the controller stamps this Deployment's pod template to roll the pods. Null leaves
    ///     restarts to the operator.
    /// </summary>
    public string? DeploymentName { get; init; }
}

/// <summary>
///     Writes the rendered config to a ConfigMap and the converted certificates to a Secret, with server-side
///     apply so this controller owns exactly those fields. Identical content is not written twice.
/// </summary>
public sealed class OutputWriter
{
    /// <summary>A ConfigMap is limited to 1 MiB by the API server; leave room for the metadata.</summary>
    internal const int MaxConfigBytes = 900 * 1024;

    private readonly IKubeApi _api;
    private readonly OutputOptions _options;
    private string? _lastDigest;

    public OutputWriter(IKubeApi api, OutputOptions options)
    {
        _api = api;
        _options = options;
    }

    /// <summary>Forget what was written, so the next call writes again (after a leadership change).</summary>
    public void Invalidate() => _lastDigest = null;

    /// <summary>Applies the ConfigMap and the Secret. Returns false when nothing changed since the last call.</summary>
    public async Task<bool> ApplyAsync(
        byte[] configBytes,
        IReadOnlyDictionary<string, byte[]> tlsFiles,
        string listenerSignature,
        CancellationToken cancellationToken)
    {
        if (configBytes.Length > MaxConfigBytes)
        {
            throw new InvalidOperationException($"The rendered config is {configBytes.Length} bytes, above the {MaxConfigBytes} byte ConfigMap budget.");
        }

        var digest = Digest(configBytes, tlsFiles);
        if (string.Equals(digest, _lastDigest, StringComparison.Ordinal))
        {
            return false;
        }

        // Certificates first: a config that names a PFX the pod cannot see yet only fails its reload, and the
        // directory watch picks the file up when the Secret volume refreshes.
        var data = new JsonObject();
        foreach (var file in tlsFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            data[file.Key] = Convert.ToBase64String(file.Value);
        }

        var secret = new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = _options.TlsSecretName, ["namespace"] = _options.Namespace },
            ["type"] = "Opaque",
            ["data"] = data,
        };
        await ApplyObjectAsync($"/api/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/secrets/{Uri.EscapeDataString(_options.TlsSecretName)}", secret, cancellationToken).ConfigureAwait(false);

        var configMap = new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "ConfigMap",
            ["metadata"] = new JsonObject { ["name"] = _options.ConfigMapName, ["namespace"] = _options.Namespace },
            ["data"] = new JsonObject { [_options.ConfigKey] = Encoding.UTF8.GetString(configBytes) },
        };
        await ApplyObjectAsync($"/api/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/configmaps/{Uri.EscapeDataString(_options.ConfigMapName)}", configMap, cancellationToken).ConfigureAwait(false);

        await EnsureListenersAsync(listenerSignature, cancellationToken).ConfigureAwait(false);
        _lastDigest = digest;
        return true;
    }

    /// <summary>The pod-template annotation that records which listener set the running pods were started with.</summary>
    public const string ListenersAnnotation = "gateway.titaniumproxy.com/listeners";

    /// <summary>The signature of a config with no listeners (what a freshly installed chart starts with).</summary>
    public const string NoListeners = "none";

    private async Task EnsureListenersAsync(string signature, CancellationToken cancellationToken)
    {
        if (_options.DeploymentName is null)
        {
            return;
        }

        var path = $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/deployments/{Uri.EscapeDataString(_options.DeploymentName)}";
        var current = await _api.SendAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        if (current.IsNotFound)
        {
            return;
        }

        if (!current.IsSuccess)
        {
            throw new KubeApiException(current.Status, current.Body);
        }

        using var document = JsonDocument.Parse(current.Body);
        var recorded = document.RootElement.GetString("spec", "template", "metadata", "annotations", ListenersAnnotation);
        if (string.Equals(recorded ?? NoListeners, signature, StringComparison.Ordinal))
        {
            return;
        }

        var patch = new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject { ["annotations"] = new JsonObject { [ListenersAnnotation] = signature } },
                },
            },
        };
        var response = await _api.SendAsync(HttpMethod.Patch, path, patch.ToJsonString(), "application/merge-patch+json", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new KubeApiException(response.Status, response.Body);
        }
    }

    /// <summary>The pod annotation the controller bumps to make the kubelet refresh the mounted ConfigMap and Secret.</summary>
    public const string PokeAnnotation = "gateway.titaniumproxy.com/config-poke";

    /// <summary>
    ///     Asks the kubelet to refresh the config volumes now. A mounted ConfigMap otherwise reaches a pod after the
    ///     kubelet's next periodic sync, up to about a minute; a pod update triggers that sync at once. Best effort:
    ///     returns the number of pods that could not be annotated (a pod that is gone is not a failure).
    /// </summary>
    public async Task<int> PokePodsAsync(IEnumerable<string> podNames, string token, CancellationToken cancellationToken)
    {
        var patch = new JsonObject
        {
            ["metadata"] = new JsonObject { ["annotations"] = new JsonObject { [PokeAnnotation] = token } },
        }.ToJsonString();
        var failed = 0;
        foreach (var name in podNames.Distinct(StringComparer.Ordinal))
        {
            var path = $"/api/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/pods/{Uri.EscapeDataString(name)}";
            try
            {
                var response = await _api.SendAsync(HttpMethod.Patch, path, patch, "application/merge-patch+json", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess && !response.IsNotFound)
                {
                    failed++;
                }
            }
            catch (HttpRequestException)
            {
                failed++;
            }
        }

        return failed;
    }

    /// <summary>A short, stable fingerprint of the listeners that need a process restart to change.</summary>
    public static string SignatureOf(IEnumerable<Titanium.Web.Proxy.Configuration.Models.ListenerConfig> listeners)
    {
        var parts = listeners
            .OrderBy(l => l.Port)
            .Select(l => $"{l.Host}:{l.Port}:{l.Type}:{(l.DecryptSsl ? "tls" : "plain")}")
            .ToList();
        return parts.Count == 0
            ? NoListeners
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))[..16];
    }

    private async Task ApplyObjectAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        var url = $"{path}?fieldManager={Uri.EscapeDataString(_options.FieldManager)}&force=true";
        var response = await _api.SendAsync(HttpMethod.Patch, url, body.ToJsonString(), "application/apply-patch+yaml", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new KubeApiException(response.Status, response.Body);
        }
    }

    private static string Digest(byte[] config, IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(config);
        foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes("\0" + file.Key + "\0"));
            hash.AppendData(file.Value);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
