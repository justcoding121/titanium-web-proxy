using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Inspector.Tests;

[TestClass]
public class GrpcJsonTranscodeSnapshotTests
{
    [TestMethod]
    public void SessionSearch_MatchesIsTranscoded()
    {
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "http://localhost/v1/greeter/x",
            IsTranscoded = true,
            ClientMethod = "GET",
            ClientPathAndQuery = "/v1/greeter/x",
            UpstreamPath = "/helloworld.Greeter/SayHello"
        };

        Assert.IsTrue(SessionSearch.Matches(snap, "is:transcoded"));
        Assert.IsFalse(SessionSearch.Matches(snap, "is:ws"));
    }

    [TestMethod]
    public void Mark_TryGet_RoundTrips()
    {
        var mark = new GrpcJsonTranscodeSessionMark
        {
            ClientMethod = "GET",
            ClientPathAndQuery = "/v1/greeter/x",
            UpstreamMethod = "POST",
            UpstreamPath = "/helloworld.Greeter/SayHello"
        };
        Assert.IsTrue(GrpcJsonTranscodeSessionMark.TryGet(mark, out var got));
        Assert.AreEqual("GET", got!.ClientMethod);
    }
}
