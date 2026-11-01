using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Plus.ControlPlane;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Dashboard;

/// <summary>What an edit request wants done to one route or cluster.</summary>
internal enum DashboardEditAction
{
    /// <summary>Check the candidate against the validator and change nothing.</summary>
    Validate,

    /// <summary>Validate, apply live, and persist in the overlay.</summary>
    Save,

    /// <summary>Remove the item in the overlay and apply live.</summary>
    Delete,
}

/// <summary>The outcome of one edit request: an HTTP status and the JSON body the page shows.</summary>
internal sealed record DashboardEditOutcome(int Status, string Result, string Json, IReadOnlyList<string> Errors);

/// <summary>
///     Operator edits of one route or one cluster. The page sends the item as JSON in the same shape the snapshot PUT
///     accepts; this class parses it, validates it with the host's validator, and hands it to the
///     <see cref="IConfigEditor" />, which persists the overlay and applies it under the config gate. Secrets are never
///     literal in these objects (they are <c>...Env</c> or <c>...File</c> references), so showing them is safe.
/// </summary>
internal static class DashboardEditing
{
    public const string RouteKind = "route";
    public const string ClusterKind = "cluster";

    /// <summary>Most bytes accepted in one edit body.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Pretty = new(ControlPlaneServer.JsonOptions)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Compact = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsKind(string kind) =>
        kind.Equals(RouteKind, StringComparison.Ordinal) || kind.Equals(ClusterKind, StringComparison.Ordinal);

    public static string ToEditableJson(RouteConfig route) => JsonSerializer.Serialize(route, Pretty);

    public static string ToEditableJson(ClusterConfig cluster) => JsonSerializer.Serialize(cluster, Pretty);

    /// <summary>A starting point for "add": the smallest object the parser accepts.</summary>
    public static string Template(string kind) => kind == RouteKind
        ? "{\n  \"id\": \"\",\n  \"match\": { \"hosts\": [], \"pathPrefix\": \"/\" },\n  \"clusterId\": \"\"\n}"
        : "{\n  \"id\": \"\",\n  \"destinations\": [\n    { \"id\": \"\", \"address\": \"127.0.0.1\", \"port\": 8080 }\n  ]\n}";

    /// <summary>
    ///     Runs one edit. <paramref name="currentRoutes" /> and <paramref name="currentClusters" /> are the live lists, used
    ///     to build the candidate for a validate-only request and to refuse an "add" that would overwrite an item.
    /// </summary>
    public static async Task<DashboardEditOutcome> RunAsync(
        IConfigEditor editor,
        string kind,
        DashboardEditAction action,
        string? existingId,
        string body,
        IReadOnlyList<RouteConfig> currentRoutes,
        IReadOnlyList<ClusterConfig> currentClusters,
        string actor,
        CancellationToken cancellationToken)
    {
        if (action == DashboardEditAction.Delete)
        {
            if (string.IsNullOrEmpty(existingId))
            {
                return Fail(400, "id is required to delete");
            }

            var known = kind == RouteKind
                ? currentRoutes.Any(r => r.Id == existingId)
                : currentClusters.Any(c => c.Id == existingId);
            if (!known)
            {
                return Fail(404, $"{kind} '{existingId}' does not exist");
            }

            return Map(await editor.ApplyEditAsync(
                new ConfigEdit
                {
                    Kind = kind == RouteKind ? ConfigEditKind.RemoveRoute : ConfigEditKind.RemoveCluster,
                    Id = existingId,
                }, actor, cancellationToken).ConfigureAwait(false));
        }

        RouteConfig? route = null;
        ClusterConfig? cluster = null;
        string? id;
        try
        {
            if (kind == RouteKind)
            {
                route = JsonSerializer.Deserialize<RouteConfig>(body, ControlPlaneServer.JsonOptions);
                id = route?.Id;
            }
            else
            {
                cluster = JsonSerializer.Deserialize<ClusterConfig>(body, ControlPlaneServer.JsonOptions);
                id = cluster?.Id;
            }
        }
        catch (JsonException ex)
        {
            return Fail(400, "invalid JSON: " + ex.Message);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return Fail(400, "invalid " + kind + ": " + ex.Message);
        }

        if (route is null && cluster is null)
        {
            return Fail(400, "the body must be a JSON object");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return Fail(400, "\"id\" is required");
        }

        if (!string.IsNullOrEmpty(existingId) && !string.Equals(id, existingId, StringComparison.Ordinal))
        {
            return Fail(400, $"the id cannot change ('{existingId}' to '{id}'): delete it and add the new one");
        }

        var exists = kind == RouteKind
            ? currentRoutes.Any(r => r.Id == id)
            : currentClusters.Any(c => c.Id == id);
        if (string.IsNullOrEmpty(existingId) && exists && action == DashboardEditAction.Save)
        {
            return Fail(409, $"{kind} '{id}' already exists: open it to edit instead");
        }

        if (action == DashboardEditAction.Validate)
        {
            var errors = kind == RouteKind
                ? editor.ValidateCandidate(Replace(currentRoutes, route!, r => r.Id), null)
                : editor.ValidateCandidate(null, Replace(currentClusters, cluster!, c => c.Id));
            return errors.Count > 0
                ? new DashboardEditOutcome(422, "rejected", Serialize(false, errors, null), errors)
                : new DashboardEditOutcome(200, "validated", Serialize(true, [], null), []);
        }

        return Map(await editor.ApplyEditAsync(
            new ConfigEdit
            {
                Kind = kind == RouteKind ? ConfigEditKind.UpsertRoute : ConfigEditKind.UpsertCluster,
                Route = route,
                Cluster = cluster,
            }, actor, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<DashboardEditOutcome> ClearOverlayAsync(IConfigEditor editor, string actor, CancellationToken cancellationToken) =>
        Map(await editor.ClearOverlayAsync(actor, cancellationToken).ConfigureAwait(false));

    private static List<T> Replace<T>(IReadOnlyList<T> current, T item, Func<T, string> idOf)
    {
        var id = idOf(item);
        var list = current.Where(existing => idOf(existing) != id).ToList();
        list.Add(item);
        return list;
    }

    private static DashboardEditOutcome Map(ConfigEditResult result) => result.Applied
        ? new DashboardEditOutcome(200, "ok", Serialize(true, [], result.OverlayDigest), [])
        : new DashboardEditOutcome(422, "rejected", Serialize(false, result.Errors ?? ["rejected"], null), result.Errors ?? ["rejected"]);

    private static DashboardEditOutcome Fail(int status, string error) =>
        new(status, "rejected", Serialize(false, [error], null), [error]);

    private static string Serialize(bool ok, IReadOnlyList<string> errors, string? digest) =>
        JsonSerializer.Serialize(new { ok, errors, digest }, Compact);
}
