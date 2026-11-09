using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

/// <summary>
///     ResourceParsers Service/Secret edges and GatewayOptions.ParsePortMap faults.
/// </summary>
[TestClass]
public sealed class ResourceParsersAndPortMapBehaviorTests
{
    [TestMethod]
    public void ResourceParsers_Service_Secret_AndMissingMetadata()
    {
        Assert.IsNull(ResourceParsers.Service(Parse("""{"metadata":{"name":"svc"}}""")));
        Assert.IsNull(ResourceParsers.Secret(Parse("""{"metadata":{"namespace":"ns"}}""")));

        var service = ResourceParsers.Service(Parse("""
            {
              "metadata":{"name":"svc","namespace":"ns","annotations":{"a":"b"}},
              "spec":{"type":"LoadBalancer","clusterIP":"10.0.0.1","ports":[
                {"name":"http","port":80,"targetPort":"http"},
                {"port":443,"targetPort":8443},
                {"name":"bad"}
              ]},
              "status":{"loadBalancer":{"ingress":[{"ip":"1.2.3.4"},{"hostname":"lb.example"},{}]}}
            }
            """));
        Assert.IsNotNull(service);
        Assert.AreEqual(2, service!.Ports.Count);
        Assert.AreEqual("http", service.Ports[0].TargetPortName);
        Assert.AreEqual(8443, service.Ports[1].TargetPort);
        Assert.AreEqual(2, service.LoadBalancerAddresses.Count);
        Assert.IsFalse(service.LoadBalancerAddresses[0].IsHostname);
        Assert.IsTrue(service.LoadBalancerAddresses[1].IsHostname);
        Assert.AreEqual("b", service.Annotations["a"]);

        var crt = Convert.ToBase64String(Encoding.UTF8.GetBytes("cert"));
        var secret = ResourceParsers.Secret(Parse(
            "{\"metadata\":{\"name\":\"tls\",\"namespace\":\"ns\",\"resourceVersion\":\"9\"},\"type\":\"kubernetes.io/tls\"," +
            "\"data\":{\"tls.crt\":\"" + crt + "\",\"tls.key\":\"!!!\",\"ca.crt\":\"" + crt + "\"}}"));
        Assert.IsNotNull(secret);
        Assert.IsNotNull(secret!.Certificate);
        Assert.IsNull(secret.PrivateKey, "invalid base64 must decode to null");
        Assert.IsNotNull(secret.CaCertificate);
        Assert.AreEqual("9", secret.ResourceVersion);
    }

    [TestMethod]
    public void ParsePortMap_RejectsDuplicatesAndUnsupportedProtocol()
    {
        var (tcp, udp) = GatewayOptions.ParsePortMap("80=8080,53/udp=5353");
        Assert.AreEqual(8080, tcp[80]);
        Assert.AreEqual(5353, udp[53]);

        var dup = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.ParsePortMap("80=8080,80=9090"));
        StringAssert.Contains(dup.Message, "more than once");

        var proto = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.ParsePortMap("53/sctp=5353"));
        StringAssert.Contains(proto.Message, "unsupported protocol");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
