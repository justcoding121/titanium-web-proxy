using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;

namespace Titanium.Xds.Tests;

[TestClass]
public class EndpointTranslatorGoldenTests
{
    [TestMethod]
    public void EmptyClusterName_NacksWithFieldPath()
    {
        // Bug class: unnamed EDS resources overwrite each other's destinations.
        var result = EndpointTranslator.Translate(new ClusterLoadAssignment());
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cluster_name", result.Error!.FieldPath);
    }

    [TestMethod]
    public void MissingSocketAddress_NacksWithFieldPath()
    {
        var assignment = new ClusterLoadAssignment { ClusterName = "c1" };
        assignment.Endpoints.Add(new LocalityLbEndpoints
        {
            LbEndpoints = { new LbEndpoint() },
        });

        var result = EndpointTranslator.Translate(assignment);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "endpoint.address");
    }

    [TestMethod]
    public void ZeroPort_NacksWithFieldPath()
    {
        var assignment = new ClusterLoadAssignment { ClusterName = "c1" };
        assignment.Endpoints.Add(new LocalityLbEndpoints
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
        });

        var result = EndpointTranslator.Translate(assignment);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "port_value");
    }

    [TestMethod]
    public void MissingWeight_DefaultsToOne_AndCapturesZonePriority()
    {
        // Bug class: a missing LoadBalancingWeight becomes weight 0 (no traffic) instead of 1.
        var assignment = new ClusterLoadAssignment { ClusterName = "c1" };
        assignment.Endpoints.Add(new LocalityLbEndpoints
        {
            Priority = 2,
            Locality = new Locality { Zone = "us-east-1a" },
            LbEndpoints =
            {
                new LbEndpoint
                {
                    Endpoint = new Endpoint
                    {
                        Address = new Address
                        {
                            SocketAddress = new SocketAddress { Address = "10.0.0.9", PortValue = 8443 },
                        },
                    },
                },
            },
        });

        var result = EndpointTranslator.Translate(assignment);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(1, result.Value!.Count);
        Assert.AreEqual(1, result.Value[0].Weight);
        Assert.AreEqual(2, result.Value[0].Priority);
        Assert.AreEqual("us-east-1a", result.Value[0].Zone);
        Assert.AreEqual(8443, result.Value[0].Port);
        Assert.AreEqual("10.0.0.9", result.Value[0].Address);
    }

    [TestMethod]
    public void ExplicitZeroWeightWrapper_StillDefaultsToOne()
    {
        // Bug class: protobuf default 0 for an explicit UInt32Value(0) is treated as weight 0.
        var assignment = new ClusterLoadAssignment { ClusterName = "c1" };
        assignment.Endpoints.Add(new LocalityLbEndpoints
        {
            LbEndpoints =
            {
                new LbEndpoint
                {
                    LoadBalancingWeight = 0,
                    Endpoint = new Endpoint
                    {
                        Address = new Address
                        {
                            SocketAddress = new SocketAddress { Address = "10.0.0.2", PortValue = 80 },
                        },
                    },
                },
            },
        });

        var result = EndpointTranslator.Translate(assignment);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(1, result.Value![0].Weight);
    }
}
