using Grpc.Core;
using Titanium.Web.Proxy.RpsLoadProbe.GrpcEcho;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal sealed class EchoGrpcService : Echo.EchoBase
{
    public override Task<EchoResponse> UnaryEcho(EchoRequest request, ServerCallContext context) =>
        Task.FromResult(new EchoResponse { Message = request.Message ?? "" });
}
