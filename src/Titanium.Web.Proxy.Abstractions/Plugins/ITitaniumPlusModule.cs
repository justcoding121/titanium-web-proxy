using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>Activation bag when Cli loads Plus via ALC and calls <see cref="ITitaniumPlusModule.Apply"/>.</summary>
public sealed class PlusActivationContext
{
    public required object ProxyServer { get; init; }
    public IClusterManager? ClusterManager { get; init; }
    public IList<IProxyMiddleware>? Middleware { get; init; }
    public ILatencyRecorder? LatencyRecorder { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>Enterprise Plus plugin entry (ALC). Inspector must never call Apply.</summary>
public interface ITitaniumPlusModule
{
    /// <summary>Minimum Abstractions assembly version this Plus build requires (e.g. 7.0.0).</summary>
    Version RequiredAbstractionsVersion { get; }

    void Apply(PlusActivationContext context);
}

/// <summary>Optional latency hook; call only when non-null.</summary>
public interface ILatencyRecorder
{
    void Record(string name, TimeSpan duration);
}

/// <summary>Optional gRPC-Web transcoding hook; Core never embeds protobuf.</summary>
public interface IGrpcTranscodeHook
{
    bool TryTranscode(ReadOnlySpan<byte> requestBody, out byte[]? responseBody);
}

/// <summary>Context for Inspector Plus panels.</summary>
public sealed class InspectorPanelContext
{
    public required object HostWindow { get; init; }
    public IServiceProvider? Services { get; init; }
}

/// <summary>Plus contributes Inspector UI panels only — never call Apply from Inspector.</summary>
public interface IPlusInspectorViewProvider
{
    Version RequiredAbstractionsVersion { get; }
    IReadOnlyList<object> CreatePanels(InspectorPanelContext context);
}
