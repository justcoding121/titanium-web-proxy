namespace Titanium.Xds;

public static class XdsTypeUrls
{
    internal const string Cluster = "type.googleapis.com/envoy.config.cluster.v3.Cluster";
    internal const string ClusterLoadAssignment = "type.googleapis.com/envoy.config.endpoint.v3.ClusterLoadAssignment";
    internal const string RouteConfiguration = "type.googleapis.com/envoy.config.route.v3.RouteConfiguration";
    internal const string Secret = "type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.Secret";
    internal const string Listener = "type.googleapis.com/envoy.config.listener.v3.Listener";

    internal static readonly string[] ApplyOrder =
    [
        Cluster,
        ClusterLoadAssignment,
        Secret,
        RouteConfiguration,
        Listener,
    ];
}
