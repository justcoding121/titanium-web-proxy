using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Remaining SessionRawHttp text-body / host-injection / non-seekable output edges and
///     HarBodyCopy malformed / short base64 leftovers still thin on Sonar develop.
/// </summary>
[TestClass]
public class SessionRawHttpBodyAndHarBase64CoverageBehaviorTests
{
    [TestMethod]
    public void SessionRawHttp_UsesTextBodies_InjectsHost_AndNonSeekableLengthIsZero()
    {
        var snap = new SessionSnapshot
        {
            Id = 11,
            Method = "  post ",
            Url = "/relative",
            Host = "host.example",
            RequestHeadersText = "Content-Type: text/plain\n",
            RequestBodyText = "req-body",
            StatusCode = 200,
            ResponseHeadersText = "X-R: 1",
            ResponseBodyText = "resp-body",
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 0);
        Assert.IsFalse(result.Truncated);
        Assert.IsTrue(result.BytesWritten > 0);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "POST /relative HTTP/1.1");
        StringAssert.Contains(text, "Host: host.example");
        StringAssert.Contains(text, "req-body");
        StringAssert.Contains(text, "HTTP/1.1 200 OK");
        StringAssert.Contains(text, "resp-body");

        var tunnel = new SessionSnapshot
        {
            Id = 12,
            Method = "CONNECT",
            IsTunnel = true,
            Url = "",
            Host = "tunnel.example:443",
            RequestHeadersText = "",
        };
        using var tunnelOut = new MemoryStream();
        _ = SessionRawHttp.Write(tunnel, tunnelOut);
        StringAssert.Contains(Encoding.UTF8.GetString(tunnelOut.ToArray()), "CONNECT tunnel.example:443 HTTP/1.1");

        using var nonSeek = new NonSeekableSink();
        var nonSeekResult = SessionRawHttp.Write(
            new SessionSnapshot
            {
                Id = 13,
                Method = "GET",
                Url = "https://n.example/",
                StatusCode = 204,
                ResponseHeadersText = "X: 1\r\n",
            },
            nonSeek);
        Assert.AreEqual(0, nonSeekResult.BytesWritten);
        Assert.IsTrue(nonSeek.Bytes.Count > 0);
    }

    [TestMethod]
    public void HarBodyCopy_ShortAndMalformedBase64_FallBackWithoutThrowing()
    {
        static (bool Truncated, byte[] Bytes) Copy(string contentObject, long max = 1024)
        {
            var json = "{\"log\":{\"entries\":[{\"response\":{\"content\":{" + contentObject + "}}}]}}";
            using var har = new MemoryStream(Encoding.UTF8.GetBytes(json));
            using var output = new MemoryStream();
            var truncated = HarBodyCopy.CopyContent(har, output, max);
            return (truncated, output.ToArray());
        }

        // "YQ" is not a full quad; decoder must not throw. Short input is treated as truncated
        // and may write zero decoded bytes.
        var shortB64 = Copy("\"text\":\"YQ\",\"encoding\":\"base64\"");
        Assert.IsTrue(shortB64.Truncated);
        Assert.IsNotNull(shortB64.Bytes);

        var bad = Copy("\"encoding\":\"base64\",\"text\":\"@@@@\"");
        Assert.IsTrue(bad.Truncated);
        Assert.IsTrue(bad.Bytes.Length > 0);

        var capped = Copy("\"encoding\":\"base64\",\"text\":\"aGVsbG8gd29ybGQh\"", max: 5);
        Assert.IsTrue(capped.Truncated || capped.Bytes.Length <= 5);
        Assert.IsTrue(capped.Bytes.Length <= 5);

        using var postHar = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"request\":{\"postData\":{\"encoding\":\"base64\",\"text\":\"Zm9v\"}}}"));
        using var postOut = new MemoryStream();
        _ = HarBodyCopy.CopyProperty(postHar, "postData", "text", postOut, 64, encoding: "base64");
        Assert.AreEqual("foo", Encoding.UTF8.GetString(postOut.ToArray()));
    }

    [TestMethod]
    public void SessionDisplayFormat_ByteSizeKbMbBranches_AndRoundMs()
    {
        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("9.5 KB", SessionDisplayFormat.FormatByteSize(9728));
            Assert.AreEqual("12 KB", SessionDisplayFormat.FormatByteSize(12 * 1024));
            Assert.AreEqual("1.5 MB", SessionDisplayFormat.FormatByteSize((long)(1.5 * 1024 * 1024)));
            Assert.AreEqual("12 MB", SessionDisplayFormat.FormatByteSize(12L * 1024 * 1024));
        }
        Assert.AreEqual(1.3, SessionDisplayFormat.RoundMs(1.26));
        Assert.AreEqual(HttpStatusClass.Success, SessionDisplayFormat.GetStatusClass(200));
        Assert.AreEqual(HttpStatusClass.ClientError, SessionDisplayFormat.GetStatusClass(404));
        Assert.AreEqual(HttpStatusClass.Informational, SessionDisplayFormat.GetStatusClass(101));
        Assert.AreEqual(HttpStatusClass.Redirection, SessionDisplayFormat.GetStatusClass(302));
        Assert.AreEqual(HttpStatusClass.ServerError, SessionDisplayFormat.GetStatusClass(503));
        Assert.AreEqual(HttpStatusClass.Pending, SessionDisplayFormat.GetStatusClass(null));
        Assert.AreEqual(HttpStatusClass.Other, SessionDisplayFormat.GetStatusClass(999));
    }

    private sealed class NonSeekableSink : Stream
    {
        public List<byte> Bytes { get; } = [];

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Bytes.Add(buffer[offset + i]);
            }
        }
    }
}
