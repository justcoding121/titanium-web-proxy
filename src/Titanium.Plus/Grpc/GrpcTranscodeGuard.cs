using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Grpc;

/// <summary>Starts gRPC-JSON transcoding when <c>grpc.transcode.enabled</c> is set.</summary>
public sealed class GrpcTranscodeGuard
{
    public IGrpcJsonTranscoder? Transcoder { get; }

    private GrpcTranscodeGuard(IGrpcJsonTranscoder transcoder) => Transcoder = transcoder;

    public static GrpcTranscodeGuard? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!IsTruthy(options, "grpc.transcode.enabled"))
            return null;

        if (!options.TryGetValue("grpc.transcode.descriptorSet", out var descriptorPath) ||
            string.IsNullOrWhiteSpace(descriptorPath))
        {
            throw new InvalidOperationException(
                "grpc.transcode.enabled requires grpc.transcode.descriptorSet (path to a FileDescriptorSet .pb).");
        }

        if (!File.Exists(descriptorPath))
            throw new FileNotFoundException($"gRPC-JSON transcoder descriptor set not found: {descriptorPath}", descriptorPath);

        if (!options.TryGetValue("grpc.transcode.services", out var servicesCsv) ||
            string.IsNullOrWhiteSpace(servicesCsv))
        {
            throw new InvalidOperationException(
                "grpc.transcode.enabled requires grpc.transcode.services (comma-separated fully-qualified service names).");
        }

        var transcoder = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            descriptorPath,
            servicesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            convertGrpcStatus: IsTruthy(options, "grpc.transcode.convertGrpcStatus", defaultValue: true),
            ignoreUnknownQueryParameters: IsTruthy(options, "grpc.transcode.ignoreUnknownQueryParameters", defaultValue: true),
            preserveProtoFieldNames: IsTruthy(options, "grpc.transcode.preserveProtoFieldNames"),
            alwaysPrintPrimitiveFields: IsTruthy(options, "grpc.transcode.alwaysPrintPrimitiveFields"));

        context.GrpcJsonTranscoder = transcoder;
        PlusLog.Info(context,
            $"Plus gRPC-JSON transcoder: enabled (descriptor={Path.GetFileName(descriptorPath)}, services={servicesCsv}).");
        return new GrpcTranscodeGuard(transcoder);
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, string> options, string key, bool defaultValue = false)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
