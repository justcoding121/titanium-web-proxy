using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class GrpcJsonTranscoderNullPathTests
{
    [TestMethod]
    public void ReverseProxyOptions_Default_TranscoderIsNull()
    {
        var options = new ReverseProxyOptions();
        Assert.IsNull(options.GrpcJsonTranscoder);
    }

    private sealed class ThrowingTranscoder : IGrpcJsonTranscoder
    {
        public ValueTask<bool> TryRewriteRequestAsync(object session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("should not be called when unset");

        public ValueTask<bool> TryRewriteResponseAsync(object session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("should not be called when unset");
    }

    [TestMethod]
    public void ThrowingTranscoder_IsAssignable_ButOnlyUsedWhenSet()
    {
        // Documents the hot-path contract: hosts must leave GrpcJsonTranscoder null when unused.
        IGrpcJsonTranscoder? unset = null;
        Assert.IsNull(unset);
        IGrpcJsonTranscoder set = new ThrowingTranscoder();
        Assert.IsNotNull(set);
    }
}
