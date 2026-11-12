using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Direct parser coverage for Namespace/Service/EndpointSlice/Secret/ConfigMap JSON shapes.
///     Bug classes: missing metadata, named vs numeric targetPort, LB hostname vs IP, bad base64, ready-null.
/// </summary>
[TestClass]
public class ResourceParsersBehaviorTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void Namespace_RequiresName_AndCopiesLabels()
    {
        Assert.IsNull(ResourceParsers.Namespace(El("""{ "metadata": {} }""")));
        var ns = ResourceParsers.Namespace(El("""
            { "metadata": { "name": "prod", "labels": { "env": "p", "empty": null } } }
            """));
        Assert.IsNotNull(ns);
        Assert.AreEqual("prod", ns!.Name);
        Assert.AreEqual("p", ns.Labels["env"]);
        Assert.AreEqual(string.Empty, ns.Labels["empty"]);
    }

    [TestMethod]
    public void Service_ParsesPortsAnnotationsAndLoadBalancerAddresses()
    {
        Assert.IsNull(ResourceParsers.Service(El("""{ "metadata": { "name": "svc" } }""")));
        var svc = ResourceParsers.Service(El("""
            {
              "metadata": {
                "name": "web",
                "namespace": "default",
                "annotations": { "a": "1", "b": null }
              },
              "spec": {
                "type": "LoadBalancer",
                "clusterIP": "10.96.0.1",
                "ports": [
                  { "name": "http", "port": 80, "targetPort": 8080 },
                  { "name": "named", "port": 81, "targetPort": "http" },
                  { "name": "bad" }
                ]
              },
              "status": {
                "loadBalancer": {
                  "ingress": [
                    { "ip": "203.0.113.10" },
                    { "hostname": "lb.example.com" },
                    { }
                  ]
                }
              }
            }
            """));
        Assert.IsNotNull(svc);
        Assert.AreEqual(2, svc!.Ports.Count);
        Assert.AreEqual(8080, svc.Ports[0].TargetPort);
        Assert.AreEqual("http", svc.Ports[1].TargetPortName);
        Assert.AreEqual("1", svc.Annotations["a"]);
        Assert.AreEqual(string.Empty, svc.Annotations["b"]);
        Assert.AreEqual(2, svc.LoadBalancerAddresses.Count);
        Assert.IsFalse(svc.LoadBalancerAddresses[0].IsHostname);
        Assert.IsTrue(svc.LoadBalancerAddresses[1].IsHostname);
    }

    [TestMethod]
    public void EndpointSlice_RequiresServiceLabel_AndKeepsNullReady()
    {
        Assert.IsNull(ResourceParsers.EndpointSlice(El("""
            { "metadata": { "name": "s", "namespace": "default" } }
            """)));
        var slice = ResourceParsers.EndpointSlice(El("""
            {
              "metadata": {
                "name": "web-abc",
                "namespace": "default",
                "labels": { "kubernetes.io/service-name": "web" }
              },
              "addressType": "IPv4",
              "ports": [ { "name": "http", "port": 8080, "protocol": "TCP" }, { "name": "bad" } ],
              "endpoints": [
                {
                  "addresses": [ "10.0.0.1", "" ],
                  "conditions": { "ready": true },
                  "targetRef": { "kind": "Pod", "name": "web-0" },
                  "zone": "zone-a"
                },
                {
                  "addresses": [ "10.0.0.2" ],
                  "targetRef": { "kind": "Node", "name": "n1" }
                }
              ]
            }
            """));
        Assert.IsNotNull(slice);
        Assert.AreEqual("web", slice!.ServiceName);
        Assert.AreEqual(1, slice.Ports.Count);
        Assert.AreEqual(2, slice.Endpoints.Count);
        Assert.AreEqual("web-0", slice.Endpoints[0].PodName);
        Assert.AreEqual("zone-a", slice.Endpoints[0].Zone);
        Assert.IsNull(slice.Endpoints[1].Ready);
        Assert.IsNull(slice.Endpoints[1].PodName);
    }

    [TestMethod]
    public void Secret_DecodesTlsMaterial_AndToleratesBadBase64()
    {
        Assert.IsNull(ResourceParsers.Secret(El("""{ "metadata": { "name": "s" } }""")));
        var good = Convert.ToBase64String(Encoding.UTF8.GetBytes("crt"));
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("key"));
        var ca = Convert.ToBase64String(Encoding.UTF8.GetBytes("ca"));
        var secret = ResourceParsers.Secret(El($$"""
            {
              "metadata": { "name": "tls", "namespace": "default", "resourceVersion": "9" },
              "type": "kubernetes.io/tls",
              "data": { "tls.crt": "{{good}}", "tls.key": "{{key}}", "ca.crt": "{{ca}}" }
            }
            """));
        Assert.IsNotNull(secret);
        Assert.AreEqual("9", secret!.ResourceVersion);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("crt"), secret.Certificate);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("key"), secret.PrivateKey);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("ca"), secret.CaCertificate);

        var bad = ResourceParsers.Secret(El("""
            {
              "metadata": { "name": "tls", "namespace": "default" },
              "data": { "tls.crt": "%%%", "tls.key": "", "ca.crt": null }
            }
            """));
        Assert.IsNotNull(bad);
        Assert.IsNull(bad!.Certificate);
        Assert.IsNull(bad.PrivateKey);
        Assert.IsNull(bad.CaCertificate);
    }

    [TestMethod]
    public void ConfigMap_CopiesDataEntries()
    {
        Assert.IsNull(ResourceParsers.ConfigMap(El("""{ "metadata": { "name": "cm" } }""")));
        var map = ResourceParsers.ConfigMap(El("""
            {
              "metadata": { "name": "ca", "namespace": "default" },
              "data": { "ca.crt": "pem", "empty": null }
            }
            """));
        Assert.IsNotNull(map);
        Assert.AreEqual("pem", map!.Data["ca.crt"]);
        Assert.AreEqual(string.Empty, map.Data["empty"]);
    }
}
