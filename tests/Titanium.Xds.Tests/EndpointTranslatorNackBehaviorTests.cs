using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;

namespace Titanium.Xds.Tests;

/// <summary>
///     EndpointTranslator NACK and locality/zone/priority branches still thin vs golden happy paths.
/// </summary>
[TestClass]
public class EndpointTranslatorNackBehaviorTests
{
    [TestMethod]
    public void Translate_NacksMissingClusterName_Address_AndPort()
    {
        var unnamed = EndpointTranslator.Translate(new ClusterLoadAssignment());
        Assert.IsFalse(unnamed.IsSuccess);
        Assert.AreEqual("cluster_name", unnamed.Error!.FieldPath);

        var noAddress = EndpointTranslator.Translate(new ClusterLoadAssignment
        {
            ClusterName = "c1",
            Endpoints =
            {
                new LocalityLbEndpoints
                {
                    LbEndpoints = { new LbEndpoint { Endpoint = new Endpoint() } },
                },
            },
        });
        Assert.IsFalse(noAddress.IsSuccess);
        StringAssert.Contains(noAddress.Error!.FieldPath, "endpoint.address");

        var noPort = EndpointTranslator.Translate(new ClusterLoadAssignment
        {
            ClusterName = "c1",
            Endpoints =
            {
                new LocalityLbEndpoints
                {
                    LbEndpoints =
                    {
                        new LbEndpoint
                        {
                            Endpoint = new Endpoint
                            {
                                Address = new Address
                                {
                                    SocketAddress = new SocketAddress { Address = "10.0.0.1", PortValue = 0 },
                                },
                            },
                        },
                    },
                },
            },
        });
        Assert.IsFalse(noPort.IsSuccess);
        StringAssert.Contains(noPort.Error!.FieldPath, "port_value");
    }

    [TestMethod]
    public void Translate_CopiesZonePriorityAndWeight()
    {
        var result = EndpointTranslator.Translate(new ClusterLoadAssignment
        {
            ClusterName = "c1",
            Endpoints =
            {
                new LocalityLbEndpoints
                {
                    Priority = 2,
                    Locality = new Locality { Zone = "  " },
                    LbEndpoints =
                    {
                        new LbEndpoint
                        {
                            LoadBalancingWeight = 7,
                            Endpoint = new Endpoint
                            {
                                Address = new Address
                                {
                                    SocketAddress = new SocketAddress { Address = "10.0.0.2", PortValue = 8443 },
                                },
                            },
                        },
                    },
                },
                new LocalityLbEndpoints
                {
                    Priority = 0,
                    Locality = new Locality { Zone = "us-west-2a" },
                    LbEndpoints =
                    {
                        new LbEndpoint
                        {
                            Endpoint = new Endpoint
                            {
                                Address = new Address
                                {
                                    SocketAddress = new SocketAddress { Address = "10.0.0.3", PortValue = 8080 },
                                },
                            },
                        },
                    },
                },
            },
        });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Value!.Count);
        Assert.IsNull(result.Value[0].Zone, "blank zone becomes null");
        Assert.AreEqual(2, result.Value[0].Priority);
        Assert.AreEqual(7, result.Value[0].Weight);
        Assert.AreEqual("us-west-2a", result.Value[1].Zone);
        Assert.AreEqual(1, result.Value[1].Weight);
    }
}
