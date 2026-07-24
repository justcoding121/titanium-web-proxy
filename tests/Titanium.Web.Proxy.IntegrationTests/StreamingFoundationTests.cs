using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class StreamingFoundationTests
{
    [TestMethod]
    public async Task BoundedBodyPipe_WriteThenRead_RoundTrips()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 1024 * 1024);
        var data = Encoding.UTF8.GetBytes("hello world");

        await pipe.WriteAsync(data);
        pipe.CompleteWriter();

        using var ms = new MemoryStream();
        await pipe.CopyToAsync(ms);
        Assert.AreEqual("hello world", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [TestMethod]
    public async Task BoundedBodyPipe_ExceedsLimit_Throws()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 10);
        var data = new byte[20];

        await Assert.ThrowsExceptionAsync<BodySizeLimitExceededException>(
            async () => await pipe.WriteAsync(data));
    }

    [TestMethod]
    public async Task BoundedBodyPipe_UnlimitedPipe_AllowsLargeBody()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0); // unlimited
        var data = new byte[1024 * 1024]; // 1 MB

        // Should not throw
        await pipe.WriteAsync(data);
        pipe.CompleteWriter();
        Assert.AreEqual(1024 * 1024, pipe.TotalWritten);
    }

    [TestMethod]
    public async Task BoundedBodyPipe_CancellationToken_Respected()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Writing with a cancelled token should throw once the pipe blocks on backpressure
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            // 1 MB exceeds the 512 KB pause threshold, so the second internal flush will block
            // and respect the pre-cancelled token.
            var data = new byte[1024 * 1024];
            await pipe.WriteAsync(data, cts.Token);
        });
    }
}
