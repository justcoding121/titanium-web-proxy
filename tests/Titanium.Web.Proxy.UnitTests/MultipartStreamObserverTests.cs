using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MultipartStreamObserverTests
{
    private static byte[] ToBytes(string s) => Encoding.ASCII.GetBytes(s);

    [TestMethod]
    public void TryCreate_ValidContentType_ReturnsObserver()
    {
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=----FormBoundary",
            null, null);
        Assert.IsNotNull(observer);
    }

    [TestMethod]
    public void TryCreate_NonMultipart_ReturnsNull()
    {
        var observer = MultipartStreamObserver.TryCreate("application/json", null, null);
        Assert.IsNull(observer);
    }

    [TestMethod]
    public void TryCreate_NoBoundary_ReturnsNull()
    {
        var observer = MultipartStreamObserver.TryCreate("multipart/form-data", null, null);
        Assert.IsNull(observer);
    }

    [TestMethod]
    public void TryCreate_NullContentType_ReturnsNull()
    {
        var observer = MultipartStreamObserver.TryCreate(null, null, null);
        Assert.IsNull(observer);
    }

    [TestMethod]
    public void TryCreate_QuotedBoundary_Parsed()
    {
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=\"my boundary\"",
            null, null);
        Assert.IsNotNull(observer);
    }

    [TestMethod]
    public void TryCreate_BoundaryTooLong_ReturnsNull()
    {
        var longBoundary = new string('x', 71);
        var observer = MultipartStreamObserver.TryCreate(
            $"multipart/form-data; boundary={longBoundary}",
            null, null);
        Assert.IsNull(observer, "Boundary exceeding 70 chars must be rejected.");
    }

    [TestMethod]
    public void TryCreate_BoundaryExactly70Chars_ReturnsObserver()
    {
        var maxBoundary = new string('x', 70);
        var observer = MultipartStreamObserver.TryCreate(
            $"multipart/form-data; boundary={maxBoundary}",
            null, null);
        Assert.IsNotNull(observer, "Boundary of exactly 70 chars must be accepted.");
    }

    [TestMethod]
    public void Observe_SinglePart_DetectsBoundaryAndPartComplete()
    {
        var partCount = 0;
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=----boundary",
            _ => { },
            () => partCount++);
        Assert.IsNotNull(observer);

        var body = ToBytes(
            "\r\n------boundary\r\n" +
            "Content-Disposition: form-data; name=\"field\"\r\n\r\n" +
            "value\r\n" +
            "------boundary--\r\n");

        observer!.Observe(body);
        Assert.IsTrue(partCount >= 1, "At least one part should have been detected.");
    }

    [TestMethod]
    public void Observe_ClosingBoundary_ReturnsFalse()
    {
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=----boundary",
            null, null);
        Assert.IsNotNull(observer);

        var body = ToBytes(
            "\r\n------boundary\r\n" +
            "Content-Disposition: form-data; name=\"x\"\r\n\r\n" +
            "val\r\n" +
            "------boundary--\r\n");

        var active = observer!.Observe(body);
        Assert.IsFalse(active, "Observer must return false after closing boundary.");
    }

    [TestMethod]
    public void Observe_ChunkedDelivery_StillDetects()
    {
        var partCount = 0;
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=ABC",
            _ => { },
            () => partCount++);
        Assert.IsNotNull(observer);

        var body = "\r\n--ABC\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\nvalue\r\n--ABC--\r\n";
        foreach (var b in Encoding.ASCII.GetBytes(body))
            observer!.Observe(new ReadOnlySpan<byte>(new[] { b }));

        Assert.IsTrue(partCount >= 1, "Chunked delivery must still detect parts.");
    }

    [TestMethod]
    public void Observe_MultipleParts_CountsAllCompletions()
    {
        var partCount = 0;
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=BOUND",
            null,
            () => partCount++);
        Assert.IsNotNull(observer);

        var body = ToBytes(
            "\r\n--BOUND\r\n" +
            "Content-Disposition: form-data; name=\"a\"\r\n\r\n" +
            "valueA\r\n" +
            "--BOUND\r\n" +
            "Content-Disposition: form-data; name=\"b\"\r\n\r\n" +
            "valueB\r\n" +
            "--BOUND--\r\n");

        observer!.Observe(body);
        Assert.IsTrue(partCount >= 2, $"Expected at least 2 part completions, got {partCount}.");
    }

    [TestMethod]
    public void Observe_AfterFinished_ReturnsFalse()
    {
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=BOUND",
            null, null);
        Assert.IsNotNull(observer);

        var body = ToBytes("\r\n--BOUND\r\n\r\n\r\n--BOUND--\r\n");
        observer!.Observe(body);

        // After the closing boundary, further calls return false immediately.
        var result = observer.Observe(ToBytes("extra bytes"));
        Assert.IsFalse(result, "Once finished, Observe must always return false.");
    }

    [TestMethod]
    public void Observe_PartHeaders_InvokesCallback()
    {
        HeaderCollection? captured = null;
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; boundary=BOUND",
            h => captured = h,
            null);
        Assert.IsNotNull(observer);

        var body = ToBytes(
            "\r\n--BOUND\r\n" +
            "Content-Disposition: form-data; name=\"file\"\r\n" +
            "Content-Type: text/plain\r\n\r\n" +
            "hello\r\n" +
            "--BOUND--\r\n");

        observer!.Observe(body);
        Assert.IsNotNull(captured, "onPartHeaders callback must have been invoked.");
    }

    [TestMethod]
    public void TryCreate_ContentTypeWithExtraParams_ParsesBoundary()
    {
        var observer = MultipartStreamObserver.TryCreate(
            "multipart/form-data; charset=utf-8; boundary=XYZ; other=val",
            null, null);
        Assert.IsNotNull(observer, "Boundary should be parsed even with additional parameters before it.");
    }
}
