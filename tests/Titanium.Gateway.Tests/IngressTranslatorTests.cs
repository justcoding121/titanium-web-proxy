using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>Table tests for how <c>networking.k8s.io</c> Ingress becomes data-plane routes, clusters, and certificates.</summary>
[TestClass]
public class IngressTranslatorTests
{
    private const string Host = "app.test";
    private static readonly string[] ExactHostA = ["a.test"];
    private static readonly string[] TlsHostsAppAndWww = ["app.test", "www.app.test"];
    private static readonly string[] WildcardHosts = ["*"];
    private static readonly string[] PolicyOrderKinds = ["ipAllowDeny", "cors", "rateLimit", "extAuth", "maxRequestBodySize"];

    private static RenderResult Render(StateBuilder b, bool validate = true, GatewayTranslatorOptions? options = null)
    {
        var result = b.Render(options);
        if (validate)
        {
            var errors = TwpConfigValidator.Validate(result.Config);
            Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        }

        return result;
    }

    private static string? Winner(RenderResult result, string host, string path, string method = "GET", Dictionary<string, string>? headers = null) =>
        new RouteMatcher().Match(new RouteMatchContext(host, path, method, headers, null), result.Config.Routes.ToList())?.Id;

    private static IngressEvent? EventFor(RenderResult result, string reason, string ingress = "ing") =>
        result.Events.FirstOrDefault(e => e.Reason == reason && e.Name == ingress);

    private static string Id(string ingress, int rule = 0, int path = 0) => $"ingress/default/{ingress}/r{rule}p{path}";

    private static RenderResult OnePath(string type, string path, string? host = Host, Dictionary<string, string>? annotations = null, Action<StateBuilder>? arrange = null)
    {
        var b = IngressOnly();
        arrange?.Invoke(b);
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(host, IPath(path, type))], annotations));
        return Render(b);
    }

    private static (RenderResult Result, RouteConfig Route) Annotated(params (string Key, string Value)[] annotations)
    {
        var result = OnePath("Prefix", "/api", annotations: annotations.ToDictionary(a => a.Key, a => a.Value));
        return (result, result.Config.UserRoutes().Single());
    }

    private static string A(string name) => IngressAnnotations.Prefix + name;

    // ---- ownership ----

    [TestMethod]
    [DataRow("titanium", null, false, false, true, DisplayName = "named class owned by us")]
    [DataRow("other", null, false, false, false, DisplayName = "named class that does not exist")]
    [DataRow("foreign", null, false, false, false, DisplayName = "named class of another controller")]
    [DataRow(null, null, false, true, true, DisplayName = "no class, our class is the default")]
    [DataRow(null, null, false, false, false, DisplayName = "no class, no default")]
    [DataRow(null, "titanium", false, false, false, DisplayName = "legacy annotation without the option")]
    [DataRow(null, "titanium", true, false, true, DisplayName = "legacy annotation with the option")]
    [DataRow(null, "other-class", true, true, false, DisplayName = "legacy annotation naming another controller beats the default")]
    [DataRow("other", "titanium", true, false, false, DisplayName = "ingressClassName wins over the legacy annotation")]
    [DataRow(null, "other-class", false, true, true, DisplayName = "legacy annotation is ignored without the option, so the default applies")]
    public void Ownership_FollowsClassNameDefaultClassAndTheLegacyAnnotation(
        string? className, string? annotation, bool watchLegacy, bool defaultClass, bool owned)
    {
        var b = IngressOnly();
        b.IngressClasses.Clear();
        b.IngressClasses.Add(IngressClass("titanium", defaultClass));
        b.IngressClasses.Add(IngressClass("foreign", controller: "example.com/other"));
        var annotations = annotation is null ? null : new Dictionary<string, string> { [IngressApiNames.LegacyClassAnnotation] = annotation };
        b.Ingresses.Add(Ingress("ing", className, [IRule(Host, IPath("/", "Prefix"))], annotations));

        var result = Render(b, options: new GatewayTranslatorOptions
        {
            DataPlaneServiceNamespace = "titanium-system",
            DataPlaneServiceName = "titanium-data",
            WatchLegacyAnnotation = watchLegacy,
        });

        Assert.AreEqual(owned, result.StatusPatches.Any(p => p.Kind == StatusKind.Ingress), "status");
        Assert.AreEqual(owned, result.Config.UserRoutes().Count == 1, "routes");
    }

    [TestMethod]
    public void Ownership_UsesTheConfiguredControllerName()
    {
        var b = IngressOnly();
        b.IngressClasses.Clear();
        b.IngressClasses.Add(IngressClass("titanium", controller: "example.com/mine"));
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var theirs = Render(b);
        var mine = Render(b, options: new GatewayTranslatorOptions { IngressControllerName = "example.com/mine" });

        Assert.AreEqual(0, theirs.Config.UserRoutes().Count);
        Assert.AreEqual(1, mine.Config.UserRoutes().Count);
    }

    // ---- paths ----

    [TestMethod]
    [DataRow("Prefix", "/foo", "/foo", true)]
    [DataRow("Prefix", "/foo", "/foo/bar", true)]
    [DataRow("Prefix", "/foo", "/foobar", false)]
    [DataRow("Prefix", "/foo/", "/foo", true)]
    [DataRow("Prefix", "/foo/", "/foo/x", true)]
    [DataRow("Prefix", "/", "/anything/at/all", true)]
    [DataRow("Exact", "/foo", "/foo", true)]
    [DataRow("Exact", "/foo", "/foo/", false)]
    [DataRow("Exact", "/foo", "/foo/x", false)]
    [DataRow("Exact", "/foo", "/Foo", false)]
    [DataRow("ImplementationSpecific", "/foo", "/foo", true)]
    [DataRow("ImplementationSpecific", "/foo", "/foo/x", true)]
    [DataRow("ImplementationSpecific", "/foo", "/foobar", false)]
    [DataRow("ImplementationSpecific", "", "/x", true)]
    public void PathTypes_MatchAsTheIngressSpecificationSays(string type, string path, string request, bool matches)
    {
        var result = OnePath(type, path);

        Assert.AreEqual(matches ? Id("ing") : GatewayTranslator.NoMatchRouteId, Winner(result, Host, request));
    }

    [TestMethod]
    public void ImplementationSpecific_IsAPrefixMatch()
    {
        var exact = OnePath("ImplementationSpecific", "/foo").Config.UserRoutes().Single().Match;
        var prefix = OnePath("Prefix", "/foo").Config.UserRoutes().Single().Match;

        Assert.AreEqual(prefix.PathKind, exact.PathKind);
        Assert.AreEqual(prefix.Path, exact.Path);
        Assert.AreEqual(PathMatchKind.SegmentPrefix, exact.PathKind);
    }

    [TestMethod]
    public void Precedence_ExactBeatsPrefix_AndTheLongestPrefixWins()
    {
        var b = IngressOnly();
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            b.AddService("default", name, 8080, "10.0.1.1");
        }

        b.Ingresses.Add(Ingress("ing", "titanium",
        [
            IRule(Host,
                IPath("/", "Prefix", "web"),
                IPath("/api", "Prefix", "a"),
                IPath("/api", "Exact", "b"),
                IPath("/api/v2", "Prefix", "c")),
        ]));
        var result = Render(b);

        string Cluster(string path) => result.Config.Routes.Single(r => r.Id == Winner(result, Host, path)).ClusterId;
        Assert.AreEqual("default/b:8080", Cluster("/api"));
        Assert.AreEqual("default/a:8080", Cluster("/api/x"));
        Assert.AreEqual("default/c:8080", Cluster("/api/v2/x"));
        Assert.AreEqual("default/web:8080", Cluster("/z"));
    }

    [TestMethod]
    public void Paths_ThatCannotBeRouted_AreSkippedWithAnEvent()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium",
        [
            IRule(Host, IPath("no-slash", "Prefix"), IPath("/x", "Regex"), IPath("/ok", "Prefix")),
        ]));
        var result = Render(b);

        Assert.AreEqual(1, result.Config.UserRoutes().Count);
        Assert.AreEqual(Id("ing", 0, 2), result.Config.UserRoutes().Single().Id);
        Assert.AreEqual(2, result.Events.Count(e => e.Reason == "InvalidPath"));
    }

    // ---- hosts ----

    [TestMethod]
    public void Hosts_ExactBeatsWildcardBeatsAnyHost()
    {
        var b = IngressOnly();
        foreach (var name in new[] { "exact", "wild", "any" })
        {
            b.AddService("default", name, 8080, "10.0.1.1");
        }

        b.Ingresses.Add(Ingress("ing", "titanium",
        [
            IRule(null, IPath("/", "Prefix", "any")),
            IRule("*.b.test", IPath("/", "Prefix", "wild")),
            IRule("a.test", IPath("/", "Prefix", "exact")),
        ]));
        var result = Render(b);

        string Cluster(string host) => result.Config.Routes.Single(r => r.Id == Winner(result, host, "/")).ClusterId;
        Assert.AreEqual("default/exact:8080", Cluster("a.test"));
        Assert.AreEqual("default/wild:8080", Cluster("x.b.test"));
        Assert.AreEqual("default/any:8080", Cluster("c.test"));
        CollectionAssert.AreEqual(ExactHostA, result.Config.UserRoutes().Single(r => r.ClusterId == "default/exact:8080").Match.Hosts!.ToArray());
        Assert.IsNull(result.Config.UserRoutes().Single(r => r.ClusterId == "default/any:8080").Match.Hosts);
    }

    [TestMethod]
    public void Hosts_AreCaseInsensitive()
    {
        var result = OnePath("Prefix", "/", host: "App.Test");

        Assert.AreEqual(Id("ing"), Winner(result, "app.test", "/"));
    }

    // ---- default backend ----

    [TestMethod]
    public void DefaultBackend_IsTheLowestPriorityRoute_AndAnswersWhatNothingElseMatched()
    {
        var b = IngressOnly();
        b.AddService("default", "fallback", 8080, "10.0.2.1");
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/api", "Prefix"))]);
        ingress.Spec.DefaultBackend = IBackend("fallback");
        b.Ingresses.Add(ingress);
        b.Ingresses.Add(Ingress("other", "titanium", [IRule(null, IPath("/", "Prefix", "web"))]));
        var result = Render(b);

        var routes = result.Config.Routes.OrderBy(r => r.Order).ToList();
        Assert.AreEqual(GatewayTranslator.NoMatchRouteId, routes[^1].Id);
        Assert.AreEqual("ingress/default/ing/default", routes[^2].Id, "the default backend sits below every other route");
        Assert.AreEqual(Id("ing"), Winner(result, Host, "/api/x"));

        // A route written for any host at "/" outranks the default backend.
        Assert.AreEqual(Id("other"), Winner(result, Host, "/elsewhere"));
    }

    [TestMethod]
    public void DefaultBackend_ServesRequestsNoRuleMatched()
    {
        var b = IngressOnly();
        b.AddService("default", "fallback", 8080, "10.0.2.1");
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/api", "Prefix"))]);
        ingress.Spec.DefaultBackend = IBackend("fallback");
        b.Ingresses.Add(ingress);
        var result = Render(b);

        Assert.AreEqual("ingress/default/ing/default", Winner(result, Host, "/nothing"));
        Assert.AreEqual("ingress/default/ing/default", Winner(result, "unknown.test", "/"));
        Assert.AreEqual("default/fallback:8080", result.Config.Routes.Single(r => r.Id == "ingress/default/ing/default").ClusterId);
    }

    [TestMethod]
    public void DefaultBackend_OfTheOldestIngressWins_AndTheOtherIsReported()
    {
        var b = IngressOnly();
        b.AddService("default", "fallback", 8080, "10.0.2.1");
        var first = Ingress("first", "titanium", created: T0);
        first.Spec.DefaultBackend = IBackend("web");
        var second = Ingress("second", "titanium", created: T0.AddHours(1));
        second.Spec.DefaultBackend = IBackend("fallback");
        b.Ingresses.Add(second);
        b.Ingresses.Add(first);
        var result = Render(b);

        Assert.AreEqual("default/web:8080", result.Config.Routes.Single(r => r.Id == "ingress/default/first/default").ClusterId);
        Assert.IsFalse(result.Config.Routes.Any(r => r.Id == "ingress/default/second/default"));
        Assert.IsNotNull(EventFor(result, "RouteConflict", "second"));
    }

    // ---- conflicts ----

    [TestMethod]
    public void TheSameHostAndPath_IsServedByTheOldestIngress_AndTheOtherIsReported()
    {
        var b = IngressOnly();
        b.AddService("default", "newer", 8080, "10.0.3.1");
        b.Ingresses.Add(Ingress("new", "titanium", [IRule(Host, IPath("/x", "Prefix", "newer"))], created: T0.AddHours(1)));
        b.Ingresses.Add(Ingress("old", "titanium", [IRule(Host, IPath("/x/", "Prefix", "web"))], created: T0));
        var result = Render(b);

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(Id("old"), route.Id);
        Assert.AreEqual("default/web:8080", route.ClusterId);
        var conflict = EventFor(result, "RouteConflict", "new");
        Assert.IsNotNull(conflict);
        StringAssert.Contains(conflict.Message, "default/old");
    }

    // ---- TLS ----

    [TestMethod]
    public void Tls_BindsTheSecretToTheHosts_AndOpensAnHttpsListener()
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = ["app.test", "www.app.test"], SecretName = "cert" }];
        b.Ingresses.Add(ingress);
        var result = Render(b);

        var item = result.Config.Certificates!.Items.Single();
        Assert.AreEqual("/etc/titanium-tls/default.cert.pfx", item.Path);
        CollectionAssert.AreEqual(TlsHostsAppAndWww, item.Hosts.ToArray());
        Assert.AreEqual(new TlsFile("default.cert.pfx", "default", "cert"), result.TlsFiles.Single());
        Assert.AreEqual(0, result.Events.Count);

        var http = result.Config.Listeners.Single(l => l.Port == 8080);
        var https = result.Config.Listeners.Single(l => l.Port == 8443);
        Assert.IsFalse(http.DecryptSsl);
        Assert.IsTrue(https.DecryptSsl);
    }

    [TestMethod]
    public void Tls_WithoutHosts_IsTheDefaultCertificate()
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { SecretName = "cert" }];
        b.Ingresses.Add(ingress);

        var item = Render(b).Config.Certificates!.Items.Single();

        CollectionAssert.AreEqual(WildcardHosts, item.Hosts.ToArray());
    }

    [TestMethod]
    public void Tls_WithNoHttpsRoutes_PublishesNothing()
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var ingress = Ingress("ing", "titanium");
        ingress.Spec.Tls = [new IngressTls { Hosts = [Host], SecretName = "cert" }];
        b.Ingresses.Add(ingress);

        var result = Render(b);

        Assert.AreEqual(0, result.Config.Listeners.Count);
        Assert.IsNull(result.Config.Certificates);
        Assert.AreEqual(0, result.TlsFiles.Count);
    }

    [TestMethod]
    [DataRow("missing", "kubernetes.io/tls", true, true, "was not found")]
    [DataRow("opaque", "Opaque", false, false, "not a kubernetes.io/tls")]
    [DataRow("broken", "kubernetes.io/tls", false, false, "not a kubernetes.io/tls")]
    public void Tls_ASecretThatDoesNotResolve_IsReported_AndNeverBound(string secret, string type, bool cert, bool key, string expected)
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "opaque", "Opaque", false, false));
        b.Secrets.Add(new SecretInfo("default", "broken", type, cert, key));
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = [Host], SecretName = secret }];
        b.Ingresses.Add(ingress);

        var result = Render(b);

        var warning = EventFor(result, "InvalidTLS");
        Assert.IsNotNull(warning);
        Assert.AreEqual("Warning", warning.Type);
        StringAssert.Contains(warning.Message, expected);
        Assert.IsNull(result.Config.Certificates);
        Assert.IsFalse(result.Config.Listeners.Any(l => l.DecryptSsl));
        Assert.AreEqual(Id("ing"), Winner(result, Host, "/"), "plain HTTP still routes");
    }

    [TestMethod]
    public void Tls_ASecretInAnotherNamespace_IsNotReadable()
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("other", "cert", "kubernetes.io/tls", true, true));
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = [Host], SecretName = "cert" }];
        b.Ingresses.Add(ingress);

        Assert.IsNotNull(EventFor(Render(b), "InvalidTLS"));
    }

    [TestMethod]
    public void Tls_WithoutASecretName_IsReported()
    {
        var b = IngressOnly();
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = [Host] }];
        b.Ingresses.Add(ingress);

        Assert.IsNotNull(EventFor(Render(b), "InvalidTLS"));
    }

    [TestMethod]
    public void Tls_WhenThePortIsNotMapped_IsReportedAndHttpStillServes()
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var ingress = Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = [Host], SecretName = "cert" }];
        b.Ingresses.Add(ingress);

        var result = Render(b, options: new GatewayTranslatorOptions
        {
            ListenerPortMap = new Dictionary<int, int> { [80] = 8080 },
            DataPlaneServiceNamespace = "titanium-system",
            DataPlaneServiceName = "titanium-data",
        });

        Assert.IsNotNull(EventFor(result, "InvalidTLS"));
        Assert.AreEqual(8080, result.Config.Listeners.Single().Port);
    }

    [TestMethod]
    public void Ports_WhenHttpIsNotMapped_NothingIsServedAndEveryIngressIsReported()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var result = Render(b, options: new GatewayTranslatorOptions
        {
            ListenerPortMap = new Dictionary<int, int> { [8081] = 8081 },
            DataPlaneServiceNamespace = "titanium-system",
            DataPlaneServiceName = "titanium-data",
        });

        Assert.AreEqual(0, result.Config.UserRoutes().Count);
        Assert.AreEqual(0, result.Config.Listeners.Count);
        Assert.IsNotNull(EventFor(result, "PortUnavailable"));
    }

    // ---- backends ----

    [TestMethod]
    public void Backend_ByPortName_ResolvesTheServicePort()
    {
        var b = IngressOnly();
        b.Services.RemoveAll(s => s.Name == "web");
        b.Slices.RemoveAll(s => s.ServiceName == "web");
        b.Services.Add(new ServiceInfo("default", "web", "ClusterIP", "10.96.1.1", [new ServicePortInfo("http", 80, 8080, null)], [], StateBuilder.NoServiceAnnotations));
        b.Slices.Add(new EndpointSliceInfo("default", "web-a", "web", "IPv4", [new EndpointPortInfo("http", 8080, "TCP")], [new EndpointInfo(["10.0.0.5"], true)]));
        var ingress = Ingress("ing", "titanium",
        [
            new IngressRule
            {
                Host = Host,
                Http = new IngressHttpRuleValue
                {
                    Paths =
                    [
                        new IngressPath
                        {
                            Path = "/",
                            PathType = "Prefix",
                            Backend = new IngressBackend { Service = new IngressServiceBackend { Name = "web", Port = new IngressServicePort { Name = "http" } } },
                        },
                    ],
                },
            },
        ]);
        b.Ingresses.Add(ingress);

        var result = Render(b);

        Assert.AreEqual("default/web:80", result.Config.UserRoutes().Single().ClusterId);
        var destination = result.Config.Clusters.Single().Destinations.Single();
        Assert.AreEqual("10.0.0.5", destination.Address);
        Assert.AreEqual(8080, destination.Port);
    }

    [TestMethod]
    public void Backend_Resource_IsRejectedWithAnEvent_AndTheRuleAnswers500()
    {
        var b = IngressOnly();
        var path = IPath("/", "Prefix");
        path.Backend = new IngressBackend { Resource = new IngressResourceBackend { ApiGroup = "example.com", Kind = "Bucket", Name = "assets" } };
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, path)]));

        var result = Render(b);

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(500, route.DirectResponse?.Status);
        var warning = EventFor(result, "UnsupportedBackend");
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning.Message, "Bucket");
        StringAssert.Contains(warning.Message, "backend.service");
        Assert.AreEqual(0, result.Config.Clusters.Count);
    }

    [TestMethod]
    [DataRow("missing", 8080, "BackendNotFound", "was not found")]
    [DataRow("web", 9999, "BackendNotFound", "has no port 9999")]
    public void Backend_ThatDoesNotExist_Answers500_AndIsReported(string service, int port, string reason, string text)
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix", service, port))]));

        var result = Render(b);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, reason)!.Message, text);
    }

    [TestMethod]
    public void Backend_WithNoPortOrNoServiceOrResource_IsInvalid()
    {
        var b = IngressOnly();
        var noPort = IPath("/a", "Prefix");
        noPort.Backend = new IngressBackend { Service = new IngressServiceBackend { Name = "web" } };
        var neither = IPath("/b", "Prefix");
        neither.Backend = new IngressBackend();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, noPort, neither)]));

        var result = Render(b);

        Assert.AreEqual(2, result.Events.Count(e => e.Reason == "InvalidBackend"));
        Assert.IsTrue(result.Config.UserRoutes().All(r => r.DirectResponse?.Status == 500));
    }

    [TestMethod]
    public void Backend_WithNoReadyEndpoint_Answers503_WithoutAnEvent()
    {
        var b = IngressOnly();
        b.Slices.Clear();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var result = Render(b);

        Assert.AreEqual(503, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual(0, result.Events.Count);
    }

    [TestMethod]
    public void Backend_OnlyReadyEndpointsReceiveTraffic()
    {
        var b = IngressOnly();
        b.Slices.Clear();
        b.Slices.Add(new EndpointSliceInfo("default", "web-a", "web", "IPv4", [new EndpointPortInfo(null, 8080, "TCP")],
        [
            new EndpointInfo(["10.0.0.1"], true),
            new EndpointInfo(["10.0.0.2"], false),
        ]));
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var cluster = Render(b).Config.Clusters.Single();

        Assert.AreEqual("10.0.0.1", cluster.Destinations.Single().Address);
    }

    [TestMethod]
    public void Backend_SharedByTwoIngresses_IsOneCluster()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("one", "titanium", [IRule("a.test", IPath("/", "Prefix"))]));
        b.Ingresses.Add(Ingress("two", "titanium", [IRule("b.test", IPath("/", "Prefix"))]));

        Assert.AreEqual(1, Render(b).Config.Clusters.Count);
    }

    [TestMethod]
    public void Rules_WithoutAnHttpSection_AreIgnored()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium", [new IngressRule { Host = Host }]));

        var result = Render(b);

        Assert.AreEqual(0, result.Config.UserRoutes().Count);
        Assert.AreEqual(0, result.Events.Count);
    }

    // ---- status ----

    [TestMethod]
    public void Status_CarriesTheDataPlaneServiceAddress()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var patch = Render(b).StatusPatches.Single(p => p.Kind == StatusKind.Ingress);

        Assert.AreEqual("default", patch.Namespace);
        Assert.AreEqual("ing", patch.Name);
        var address = patch.Addresses!.Single();
        Assert.AreEqual("IPAddress", address.Type);
        Assert.AreEqual("203.0.113.10", address.Value);
    }

    [TestMethod]
    public void Status_ReportsAHostnameAddress()
    {
        var b = IngressOnly();
        b.Services.RemoveAll(s => s.Name == "titanium-data");
        b.Services.Add(new ServiceInfo("titanium-system", "titanium-data", "LoadBalancer", "10.96.0.9", [], [new ServiceAddress("lb.example.com", true)], StateBuilder.NoServiceAnnotations));
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))]));

        var address = Render(b).StatusPatches.Single(p => p.Kind == StatusKind.Ingress).Addresses!.Single();

        Assert.AreEqual("Hostname", address.Type);
        Assert.AreEqual("lb.example.com", address.Value);
    }

    [TestMethod]
    public void Status_IsWrittenForAnIngressWhoseRulesAreAllRejected()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix", "missing"))]));

        var result = Render(b);

        Assert.IsTrue(result.StatusPatches.Any(p => p.Kind == StatusKind.Ingress && p.Name == "ing"));
        Assert.IsNotNull(EventFor(result, "BackendNotFound"));
    }

    // ---- annotations ----

    [TestMethod]
    public void Annotations_UnknownKeysUnderThePrefix_RaiseAWarning_AndTheRouteStillServes()
    {
        var (result, route) = Annotated((A("bogus"), "x"), ("example.com/other", "y"), ("example.com/rewrite-target", "/"));

        var warning = result.Events.Single();
        Assert.AreEqual("UnknownAnnotation", warning.Reason);
        Assert.AreEqual("Warning", warning.Type);
        StringAssert.Contains(warning.Message, A("bogus"));
        Assert.IsNull(route.DirectResponse);
        Assert.AreEqual("default/web:8080", route.ClusterId);
    }

    [TestMethod]
    public void Annotations_SslRedirectFalse_IsAccepted_AndTrueIsRejectedLoudly()
    {
        var (accepted, okRoute) = Annotated((A("ssl-redirect"), "false"));
        Assert.AreEqual(0, accepted.Events.Count);
        Assert.IsNull(okRoute.DirectResponse);

        var (rejected, route) = Annotated((A("ssl-redirect"), "true"));
        Assert.AreEqual(500, route.DirectResponse?.Status);
        var warning = EventFor(rejected, "InvalidAnnotation")!;
        StringAssert.Contains(warning.Message, "ssl-redirect");
        StringAssert.Contains(warning.Message, "scheme");
    }

    [TestMethod]
    [DataRow("/api", "/", "/api/users?x=1", "/users?x=1")]
    [DataRow("/api", "/", "/api", "/")]
    [DataRow("/api", "/v2", "/api/users", "/v2/users")]
    [DataRow("/api", "/v2/", "/api/users", "/v2/users")]
    public void Annotations_RewriteTarget_ReplacesThePrefix(string prefix, string target, string input, string expected)
    {
        var result = OnePath("Prefix", prefix, annotations: new() { [A("rewrite-target")] = target });
        var transforms = result.Config.UserRoutes().Single().Transforms!;

        var ctx = new TransformRequestContext { Path = input };
        new TransformEngine().ApplyRequestTransforms(transforms, ctx);

        Assert.AreEqual(expected, ctx.Path);
    }

    [TestMethod]
    public void Annotations_RewriteTarget_OnAnExactPathReplacesTheWholePath_AndKeepsTheQuery()
    {
        var result = OnePath("Exact", "/old", annotations: new() { [A("rewrite-target")] = "/new" });
        var ctx = new TransformRequestContext { Path = "/old?a=1" };

        new TransformEngine().ApplyRequestTransforms(result.Config.UserRoutes().Single().Transforms!, ctx);

        Assert.AreEqual("/new?a=1", ctx.Path);
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("/api/$1")]
    [DataRow("/x?y=1")]
    public void Annotations_RewriteTarget_ThatCannotBeHonored_Answers500(string value)
    {
        var (result, route) = Annotated((A("rewrite-target"), value));

        Assert.AreEqual(500, route.DirectResponse?.Status);
        Assert.IsNotNull(EventFor(result, "InvalidAnnotation"));
    }

    [TestMethod]
    public void Annotations_ProxyReadTimeout_SetsTheClusterRequestTimeout_OnAClusterOfItsOwn()
    {
        var (result, route) = Annotated((A("proxy-read-timeout"), "45"));

        var cluster = result.Config.Clusters.Single();
        Assert.AreEqual(TimeSpan.FromSeconds(45), cluster.HttpClient!.RequestTimeout);
        Assert.AreEqual(cluster.Id, route.ClusterId);
        StringAssert.Contains(cluster.Id, "@ing:default/ing");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("3601")]
    [DataRow("fast")]
    public void Annotations_ProxyReadTimeout_OutOfRange_Answers500(string value) =>
        Assert.AreEqual(500, Annotated((A("proxy-read-timeout"), value)).Route.DirectResponse?.Status);

    [TestMethod]
    [DataRow("8m", 8L * 1024 * 1024)]
    [DataRow("512k", 512L * 1024)]
    [DataRow("1G", 1024L * 1024 * 1024)]
    [DataRow("1500", 1500L)]
    public void Annotations_ProxyBodySize_BecomesAMaxRequestBodySizePolicy(string value, long bytes)
    {
        var (result, route) = Annotated((A("proxy-body-size"), value));

        var policy = result.Config.Policies.Single();
        Assert.AreEqual("maxRequestBodySize", policy.Kind);
        Assert.AreEqual(bytes, policy.MaxRequestBodyBytes);
        CollectionAssert.AreEqual(new[] { policy.Name }, route.Policies!.ToArray());
    }

    [TestMethod]
    public void Annotations_ProxyBodySize_Zero_MeansNoLimit()
    {
        var (result, route) = Annotated((A("proxy-body-size"), "0"));

        Assert.AreEqual(0, result.Config.Policies.Count);
        Assert.IsNull(route.Policies);
    }

    [TestMethod]
    [DataRow("big")]
    [DataRow("8mb")]
    [DataRow("-1")]
    [DataRow("99999999999999g")]
    public void Annotations_ProxyBodySize_Invalid_Answers500(string value) =>
        Assert.AreEqual(500, Annotated((A("proxy-body-size"), value)).Route.DirectResponse?.Status);

    [TestMethod]
    [DataRow("HTTP", false)]
    [DataRow("http", false)]
    [DataRow("HTTPS", true)]
    [DataRow("H2C", false)]
    [DataRow("GRPC", false)]
    public void Annotations_BackendProtocol_SetsHowDestinationsAreReached(string value, bool tls)
    {
        var (result, route) = Annotated((A("backend-protocol"), value));

        Assert.IsTrue(result.Config.Clusters.Single().Destinations.All(d => d.UseHttps == tls));
        Assert.IsNull(route.DirectResponse);
    }

    [TestMethod]
    public void Annotations_BackendProtocol_AnythingElse_Answers500()
    {
        var (result, route) = Annotated((A("backend-protocol"), "FCGI"));

        Assert.AreEqual(500, route.DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, "InvalidAnnotation")!.Message, "HTTP, HTTPS, H2C, or GRPC");
    }

    [TestMethod]
    public void Annotations_Affinity_Cookie_PinsClientsWithAnOpaqueCookie()
    {
        var (result, _) = Annotated((A("affinity"), "cookie"));

        var affinity = result.Config.Clusters.Single().SessionAffinity!;
        Assert.AreEqual(AffinityMode.Cookie, affinity.Mode);
        Assert.AreEqual("titanium-affinity", affinity.CookieName);
    }

    [TestMethod]
    public void Annotations_Affinity_OtherThanCookie_Answers500() =>
        Assert.AreEqual(500, Annotated((A("affinity"), "ip")).Route.DirectResponse?.Status);

    [TestMethod]
    [DataRow("round_robin", LoadBalanceAlgorithm.RoundRobin)]
    [DataRow("random", LoadBalanceAlgorithm.Random)]
    [DataRow("least_conn", LoadBalanceAlgorithm.LeastRequests)]
    [DataRow("ewma", LoadBalanceAlgorithm.LeastTime)]
    [DataRow("p2c", LoadBalanceAlgorithm.PowerOfTwoChoices)]
    public void Annotations_LoadBalance_MapsToTheClusterAlgorithm(string value, LoadBalanceAlgorithm expected)
    {
        var (result, _) = Annotated((A("load-balance"), value));

        Assert.AreEqual(expected, result.Config.Clusters.Single().Algorithm);
    }

    [TestMethod]
    public void Annotations_LoadBalance_Unknown_Answers500() =>
        Assert.AreEqual(500, Annotated((A("load-balance"), "ip_hash")).Route.DirectResponse?.Status);

    [TestMethod]
    public void Annotations_WhitelistSourceRange_BecomesAnAllowList()
    {
        var (result, route) = Annotated((A("whitelist-source-range"), "10.0.0.0/8, 192.168.1.5 ,2001:db8::/32"));

        var policy = result.Config.Policies.Single();
        Assert.AreEqual("ipAllowDeny", policy.Kind);
        Assert.AreEqual("10.0.0.0/8,192.168.1.5,2001:db8::/32", policy.Allow);
        Assert.IsNull(policy.Deny);
        CollectionAssert.AreEqual(new[] { policy.Name }, route.Policies!.ToArray());
    }

    [TestMethod]
    [DataRow("10.0.0.0/33")]
    [DataRow("office")]
    [DataRow(",")]
    [DataRow("10.0.0.0/8,nope")]
    public void Annotations_WhitelistSourceRange_Invalid_FailsClosed(string value)
    {
        var (result, route) = Annotated((A("whitelist-source-range"), value));

        Assert.AreEqual(500, route.DirectResponse?.Status);
        Assert.AreEqual(0, result.Config.Policies.Count);
    }

    [TestMethod]
    public void Annotations_LimitRps_BecomesAPerClientRateLimit()
    {
        var (result, _) = Annotated((A("limit-rps"), "25"));

        var policy = result.Config.Policies.Single();
        Assert.AreEqual("rateLimit", policy.Kind);
        Assert.AreEqual(25, policy.Limit);
        Assert.AreEqual(1, policy.WindowSeconds);
        Assert.AreEqual("client", policy.Key);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("1000001")]
    [DataRow("many")]
    public void Annotations_LimitRps_OutOfRange_Answers500(string value) =>
        Assert.AreEqual(500, Annotated((A("limit-rps"), value)).Route.DirectResponse?.Status);

    [TestMethod]
    [DataRow("*")]
    [DataRow("https://app.example.com")]
    [DataRow("https://a.example.com, http://b.example.com:8080")]
    public void Annotations_CorsAllowOrigin_BecomesACorsPolicy(string value)
    {
        var (result, _) = Annotated((A("cors-allow-origin"), value));

        var policy = result.Config.Policies.Single();
        Assert.AreEqual("cors", policy.Kind);
        Assert.AreEqual(string.Join(",", value.Split(',', StringSplitOptions.TrimEntries)), policy.Origins);
    }

    [TestMethod]
    [DataRow("app.example.com")]
    [DataRow("https://app.example.com/")]
    [DataRow("https://app.example.com/path")]
    [DataRow("*,https://a.example.com")]
    [DataRow("ftp://a.example.com")]
    public void Annotations_CorsAllowOrigin_Invalid_Answers500(string value) =>
        Assert.AreEqual(500, Annotated((A("cors-allow-origin"), value)).Route.DirectResponse?.Status);

    [TestMethod]
    public void Annotations_AuthUrl_BecomesAnExtAuthPolicy_ThatFailsClosed()
    {
        var (result, route) = Annotated((A("auth-url"), "https://auth.example.com/check"));

        var policy = result.Config.Policies.Single();
        Assert.AreEqual("extAuth", policy.Kind);
        Assert.AreEqual("https://auth.example.com/check", policy.Url);
        Assert.AreEqual("GET", policy.Method);
        Assert.IsFalse(policy.FailOpen);
        CollectionAssert.AreEqual(new[] { policy.Name }, route.Policies!.ToArray());
    }

    [TestMethod]
    [DataRow("auth.example.com/check")]
    [DataRow("https://user:pw@auth.example.com/")]
    [DataRow("ftp://auth.example.com/")]
    public void Annotations_AuthUrl_Invalid_Answers500(string value) =>
        Assert.AreEqual(500, Annotated((A("auth-url"), value)).Route.DirectResponse?.Status);

    [TestMethod]
    public void Annotations_PolicyOrder_RunsAllowListAndPreflightBeforeAuth()
    {
        var (result, route) = Annotated(
            (A("proxy-body-size"), "1m"),
            (A("auth-url"), "https://auth.example.com/"),
            (A("limit-rps"), "5"),
            (A("cors-allow-origin"), "*"),
            (A("whitelist-source-range"), "10.0.0.0/8"));

        var kinds = route.Policies!.Select(n => result.Config.Policies.Single(p => p.Name == n).Kind).ToArray();
        CollectionAssert.AreEqual(PolicyOrderKinds, kinds);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
    }

    [TestMethod]
    public void Annotations_ClusterSettingsOfOneIngress_DoNotLeakToAnotherThatSharesTheService()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("plain", "titanium", [IRule("a.test", IPath("/", "Prefix"))]));
        b.Ingresses.Add(Ingress("tuned", "titanium", [IRule("b.test", IPath("/", "Prefix"))], new() { [A("load-balance")] = "random" }));

        var result = Render(b);

        Assert.AreEqual(2, result.Config.Clusters.Count);
        Assert.AreEqual(LoadBalanceAlgorithm.RoundRobin, result.Config.Clusters.Single(c => c.Id == "default/web:8080").Algorithm);
        Assert.AreEqual(LoadBalanceAlgorithm.Random, result.Config.Clusters.Single(c => c.Id.Contains("@ing:default/tuned", StringComparison.Ordinal)).Algorithm);
    }

    [TestMethod]
    public void Annotations_AnInvalidOne_MakesEveryRuleOfThatIngressAnswer500_AndNoOtherIngress()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("bad", "titanium", [IRule("a.test", IPath("/x", "Prefix"), IPath("/y", "Exact"))], new() { [A("limit-rps")] = "zero" }));
        b.Ingresses.Add(Ingress("good", "titanium", [IRule("b.test", IPath("/", "Prefix"))]));

        var result = Render(b);

        Assert.IsTrue(result.Config.UserRoutes().Where(r => r.Id.Contains("/bad/", StringComparison.Ordinal)).All(r => r.DirectResponse?.Status == 500));
        Assert.IsNull(result.Config.UserRoutes().Single(r => r.Id.Contains("/good/", StringComparison.Ordinal)).DirectResponse);
        var warning = EventFor(result, "InvalidAnnotation", "bad")!;
        StringAssert.Contains(warning.Message, A("limit-rps"));
        StringAssert.Contains(warning.Message, "500");
    }

    // ---- canary ----

    private static StateBuilder CanaryState(Dictionary<string, string> canaryAnnotations, Dictionary<string, string>? primaryAnnotations = null)
    {
        var b = IngressOnly();
        b.AddService("default", "canary", 8080, "10.0.9.1");
        b.Ingresses.Add(Ingress("main", "titanium", [IRule(Host, IPath("/shop", "Prefix"))], primaryAnnotations));
        b.Ingresses.Add(Ingress("canary", "titanium", [IRule(Host, IPath("/shop", "Prefix", "canary"))], canaryAnnotations, created: T0.AddMinutes(5)));
        return b;
    }

    [TestMethod]
    public void Canary_Weight_SplitsTheRouteBetweenThePrimaryAndTheCanary()
    {
        var result = Render(CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = "20" }));

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(Id("main"), route.Id);
        Assert.AreEqual("default/web:8080", route.ClusterId);
        var weights = route.Clusters!.ToDictionary(c => c.ClusterId, c => c.Weight);
        Assert.AreEqual(80, weights["default/web:8080"]);
        Assert.AreEqual(20, weights["default/canary:8080"]);
        Assert.IsFalse(result.Config.Routes.Any(r => r.Id.Contains("/canary/", StringComparison.Ordinal)), "the canary Ingress adds no route of its own");
    }

    [TestMethod]
    public void Canary_Weight100_SendsEverythingToTheCanary_AndZeroNothing()
    {
        var all = Render(CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = "100" })).Config.UserRoutes().Single();
        Assert.AreEqual("default/canary:8080", all.ClusterId);
        Assert.IsNull(all.Clusters);

        var none = Render(CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = "0" })).Config.UserRoutes().Single();
        Assert.AreEqual("default/web:8080", none.ClusterId);
        Assert.IsNull(none.Clusters);
    }

    [TestMethod]
    public void Canary_ByHeader_AddsRoutesThatForceTheChoice_AndTheWeightStillApplies()
    {
        var result = Render(CanaryState(new()
        {
            [A("canary")] = "true",
            [A("canary-weight")] = "10",
            [A("canary-by-header")] = "X-Canary",
        }));

        var always = result.Config.Routes.Single(r => r.Id == Id("main") + "/canary-always");
        var never = result.Config.Routes.Single(r => r.Id == Id("main") + "/canary-never");
        Assert.AreEqual("default/canary:8080", always.ClusterId);
        Assert.AreEqual("default/web:8080", never.ClusterId);
        Assert.AreEqual("always", always.Match.Headers!["X-Canary"]);
        Assert.AreEqual("never", never.Match.Headers!["X-Canary"]);

        Assert.AreEqual(Id("main") + "/canary-always", Winner(result, Host, "/shop/x", headers: new() { ["X-Canary"] = "always" }));
        Assert.AreEqual(Id("main") + "/canary-never", Winner(result, Host, "/shop/x", headers: new() { ["X-Canary"] = "never" }));
        Assert.AreEqual(Id("main"), Winner(result, Host, "/shop/x", headers: new() { ["X-Canary"] = "sometimes" }));
        Assert.AreEqual(Id("main"), Winner(result, Host, "/shop/x"));
        Assert.AreEqual(2, result.Config.UserRoutes().Single(r => r.Id == Id("main")).Clusters!.Count);
    }

    [TestMethod]
    public void Canary_TakesRouteLevelSettingsFromThePrimary()
    {
        var result = Render(CanaryState(
            new() { [A("canary")] = "true", [A("canary-weight")] = "50", [A("limit-rps")] = "1" },
            new() { [A("limit-rps")] = "99" }));

        Assert.AreEqual(99, result.Config.Policies.Single().Limit);
    }

    [TestMethod]
    public void Canary_WithoutAPrimary_IsReportedAndNotServed()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("canary", "titanium", [IRule(Host, IPath("/shop", "Prefix"))], new() { [A("canary")] = "true", [A("canary-weight")] = "50" }));

        var result = Render(b);

        Assert.AreEqual(0, result.Config.UserRoutes().Count);
        Assert.IsNotNull(EventFor(result, "CanaryWithoutPrimary", "canary"));
    }

    [TestMethod]
    public void Canary_ASecondCanaryForTheSameRoute_IsReported()
    {
        var b = CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = "10" });
        b.Ingresses.Add(Ingress("canary2", "titanium", [IRule(Host, IPath("/shop", "Prefix", "canary"))], new() { [A("canary")] = "true", [A("canary-weight")] = "30" }, created: T0.AddMinutes(9)));

        var result = Render(b);

        Assert.IsNotNull(EventFor(result, "CanaryConflict", "canary2"));
        Assert.AreEqual(10, result.Config.UserRoutes().Single().Clusters!.Single(c => c.ClusterId == "default/canary:8080").Weight);
    }

    [TestMethod]
    public void Canary_WeightOrHeaderWithoutTheCanaryAnnotation_Answers500()
    {
        var (result, route) = Annotated((A("canary-weight"), "10"));

        Assert.AreEqual(500, route.DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, "InvalidAnnotation")!.Message, "canary");
    }

    [TestMethod]
    [DataRow("101")]
    [DataRow("-1")]
    [DataRow("half")]
    public void Canary_WeightOutOfRange_IsRejected(string weight)
    {
        var result = Render(CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = weight }));

        Assert.IsNotNull(EventFor(result, "InvalidAnnotation", "canary"));
        Assert.IsNull(result.Config.UserRoutes().Single().Clusters);
    }

    [TestMethod]
    public void Canary_WithAnUnreachableBackend_TakesNoTraffic_AndIsReported()
    {
        var b = CanaryState(new() { [A("canary")] = "true", [A("canary-weight")] = "50" });
        b.Ingresses.RemoveAll(i => i.Metadata.Name == "canary");
        b.Ingresses.Add(Ingress("canary", "titanium", [IRule(Host, IPath("/shop", "Prefix", "gone"))], new() { [A("canary")] = "true", [A("canary-weight")] = "50" }));

        var result = Render(b);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.Clusters);
        Assert.AreEqual("default/web:8080", route.ClusterId);
        Assert.IsNotNull(EventFor(result, "BackendNotFound", "canary"));
    }

    // ---- upstream TLS ----

    private static StateBuilder UpstreamState(bool withCa, string? type = "kubernetes.io/tls", bool usable = true)
    {
        var b = IngressOnly();
        b.Secrets.Add(new SecretInfo("default", "backend-tls", type, usable, usable, withCa));
        return b;
    }

    [TestMethod]
    public void UpstreamTlsSecret_BecomesTheClusterTrustAnchorsAndClientCertificate()
    {
        var b = UpstreamState(withCa: true);
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("backend-protocol")] = "HTTPS", [A("upstream-tls-secret")] = "backend-tls" }));

        var result = Render(b, validate: false);

        var client = result.Config.Clusters.Single().HttpClient!;
        Assert.AreEqual("/etc/titanium-tls/default.backend-tls.upstream.crt", client.ClientCertificatePath);
        Assert.AreEqual("/etc/titanium-tls/default.backend-tls.upstream.key", client.ClientCertificateKeyPath);
        Assert.AreEqual("/etc/titanium-tls/default.backend-tls.upstream-ca.crt", client.CaBundlePath);
        Assert.IsTrue(result.Config.Clusters.Single().Destinations.All(d => d.UseHttps));
        Assert.IsTrue(client.VerifyHostname, "verification stays on");
        Assert.IsFalse(client.AcceptAnyServerCertificate);

        var files = result.TlsFiles.ToDictionary(f => f.FileName);
        Assert.AreEqual(TlsFileKind.CertificatePem, files["default.backend-tls.upstream.crt"].Kind);
        Assert.AreEqual(TlsFileKind.PrivateKeyPem, files["default.backend-tls.upstream.key"].Kind);
        Assert.AreEqual(TlsFileKind.CaPem, files["default.backend-tls.upstream-ca.crt"].Kind);
        Assert.IsNull(result.Config.Certificates, "upstream files are not serving certificates");
    }

    [TestMethod]
    public void UpstreamTlsSecret_WithoutACa_UsesTheClientCertificateOnly()
    {
        var b = UpstreamState(withCa: false);
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("backend-protocol")] = "HTTPS", [A("upstream-tls-secret")] = "default/backend-tls" }));

        var result = Render(b, validate: false);

        Assert.IsNull(result.Config.Clusters.Single().HttpClient!.CaBundlePath);
        Assert.AreEqual(2, result.TlsFiles.Count);
    }

    [TestMethod]
    public void UpstreamTlsSecret_PassesTheDataPlaneValidator_WhenTheFilesExist()
    {
        var directory = Directory.CreateTempSubdirectory("ingress-upstream-").FullName;
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=backend", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var crt = certificate.ExportCertificatePem();
            File.WriteAllText(Path.Combine(directory, "default.backend-tls.upstream.crt"), crt);
            File.WriteAllText(Path.Combine(directory, "default.backend-tls.upstream.key"), rsa.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(Path.Combine(directory, "default.backend-tls.upstream-ca.crt"), crt);
            var b = UpstreamState(withCa: true);
            b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("backend-protocol")] = "HTTPS", [A("upstream-tls-secret")] = "backend-tls" }));

            var result = Render(b, options: new GatewayTranslatorOptions
            {
                TlsMountPath = directory.Replace('\\', '/'),
                DataPlaneServiceNamespace = "titanium-system",
                DataPlaneServiceName = "titanium-data",
            });

            Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow("missing", "kubernetes.io/tls", "was not found")]
    [DataRow("opaque", "Opaque", "not a kubernetes.io/tls")]
    public void UpstreamTlsSecret_ThatDoesNotResolve_Answers500(string secret, string type, string expected)
    {
        var b = UpstreamState(withCa: true);
        b.Secrets.Add(new SecretInfo("default", "opaque", type, false, false));
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("backend-protocol")] = "HTTPS", [A("upstream-tls-secret")] = secret }));

        var result = Render(b, validate: false);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, "InvalidAnnotation")!.Message, expected);
        Assert.AreEqual(0, result.TlsFiles.Count);
    }

    [TestMethod]
    public void UpstreamTlsSecret_NeedsHttpsBackendProtocol()
    {
        var b = UpstreamState(withCa: true);
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("upstream-tls-secret")] = "backend-tls" }));

        var result = Render(b);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, "InvalidAnnotation")!.Message, "HTTPS");
    }

    [TestMethod]
    public void UpstreamTlsSecret_FromAnotherNamespace_IsRejected()
    {
        var b = UpstreamState(withCa: true);
        b.Ingresses.Add(Ingress("ing", "titanium", [IRule(Host, IPath("/", "Prefix"))], new() { [A("backend-protocol")] = "HTTPS", [A("upstream-tls-secret")] = "other/backend-tls" }));

        var result = Render(b);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        StringAssert.Contains(EventFor(result, "InvalidAnnotation")!.Message, "namespace");
    }

    // ---- determinism ----

    [TestMethod]
    public void Rendering_IsStable_WhateverTheOrderOfTheInput()
    {
        StateBuilder Build(bool reverse)
        {
            var b = IngressOnly();
            b.AddService("default", "a", 8080, "10.0.1.1");
            var items = new[]
            {
                Ingress("one", "titanium", [IRule("a.test", IPath("/", "Prefix"), IPath("/x", "Exact", "a"))], new() { [A("limit-rps")] = "5" }),
                Ingress("two", "titanium", [IRule("b.test", IPath("/", "Prefix", "a"))], new() { [A("load-balance")] = "random" }, created: T0.AddMinutes(1)),
                Ingress("three", "titanium", [IRule(null, IPath("/y", "Prefix"))], created: T0.AddMinutes(2)),
            };
            foreach (var item in reverse ? items.Reverse() : items)
            {
                b.Ingresses.Add(item);
            }

            return b;
        }

        var forward = TwpConfigLoader.ToJson(Render(Build(false)).Config);
        var backward = TwpConfigLoader.ToJson(Render(Build(true)).Config);

        Assert.AreEqual(forward, backward);
    }

    [TestMethod]
    public void NoIngress_ChangesNothingInTheRenderedConfig()
    {
        var b = Basic();
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var before = TwpConfigLoader.ToJson(b.Render().Config);
        b.IngressClasses.Add(IngressClass("titanium"));

        Assert.AreEqual(before, TwpConfigLoader.ToJson(b.Render().Config));
    }
}
