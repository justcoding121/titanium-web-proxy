using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Remaining Avalonia-safe Inspector leftovers: RunLogFormat tokens, InspectorLogFiles unique/prune,
///     ProgressiveDelivery fallback counters, and SessionRawHttp host-from-URL.
/// </summary>
[TestClass]
public class RunLogInspectorFilesAndProgressiveCoverageBehaviorTests
{
    [TestMethod]
    public void RunLogFormat_LevelTokens_AndBuildMessageWithException()
    {
        Assert.AreEqual("TRACE", RunLogFormat.LevelToToken(LogLevel.Trace));
        Assert.AreEqual("DEBUG", RunLogFormat.LevelToToken(LogLevel.Debug));
        Assert.AreEqual("INFO", RunLogFormat.LevelToToken(LogLevel.Information));
        Assert.AreEqual("WARN", RunLogFormat.LevelToToken(LogLevel.Warning));
        Assert.AreEqual("ERROR", RunLogFormat.LevelToToken(LogLevel.Error));
        Assert.AreEqual("CRIT", RunLogFormat.LevelToToken(LogLevel.Critical));
        Assert.AreEqual("NONE", RunLogFormat.LevelToToken(LogLevel.None));

        var line = RunLogFormat.FormatLine(new RunLogEntry(
            new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
            LogLevel.Warning,
            "cat",
            "hello\r\n"));
        StringAssert.Contains(line, "[WARN ]");
        StringAssert.Contains(line, "cat: hello");

        Assert.AreEqual("plain", RunLogFormat.BuildMessage("plain\n", null));
        var withEx = RunLogFormat.BuildMessage("boom", new InvalidOperationException("x"));
        StringAssert.Contains(withEx, "boom");
        StringAssert.Contains(withEx, "InvalidOperationException");
        Assert.AreEqual(
            new InvalidOperationException("solo").ToString(),
            RunLogFormat.BuildMessage("", new InvalidOperationException("solo")));
    }

    [TestMethod]
    public void InspectorLogFiles_UniquePathCollision_AndPruneGuards()
    {
        var folder = Path.Combine(Path.GetTempPath(), "twp-insp-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
            var first = InspectorLogFiles.UniquePath(folder, now);
            File.WriteAllText(first, "a");
            var second = InspectorLogFiles.UniquePath(folder, now);
            Assert.AreNotEqual(first, second);
            StringAssert.Contains(Path.GetFileName(second), "_2");
            File.WriteAllText(second, "b");
            var third = InspectorLogFiles.UniquePath(folder, now);
            StringAssert.Contains(Path.GetFileName(third), "_3");

            InspectorLogFiles.Prune(folder, keep: 0, currentFile: first);
            Assert.IsTrue(File.Exists(first));
            InspectorLogFiles.Prune(Path.Combine(folder, "missing"), keep: 2, currentFile: null);
            Assert.IsFalse(string.IsNullOrWhiteSpace(InspectorLogFiles.DefaultFolder()));
            Assert.AreEqual(
                Path.GetFullPath(folder),
                InspectorLogFiles.ResolveFolder("  " + folder + "  "));
            Assert.AreEqual(InspectorLogFiles.DefaultFolder(), InspectorLogFiles.ResolveFolder("   "));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    [TestMethod]
    public void ProgressiveDelivery_FallbackCounts_CoverEverySkipReason()
    {
        foreach (ProgressiveSkipReason reason in Enum.GetValues<ProgressiveSkipReason>())
        {
            _ = ProgressiveDelivery.FallbackCount(reason);
        }

        _ = ProgressiveDelivery.FallbackCount((ProgressiveSkipReason)999);
        _ = ProgressiveDelivery.ProgressiveCount;

        var body = Encoding.UTF8.GetBytes(new string('p', ProgressiveDelivery.MinWireBytes));
        var head = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = body,
        };
        head.Headers.AddHeader(new HttpHeader("Content-Length", body.Length.ToString()));
        var beforeHead = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Head);
        Assert.IsNull(ProgressiveDelivery.TryBuild(head, new Request { Method = "HEAD" }, body, body.Length, false, false));
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Head) > beforeHead);

        var status = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 204,
            StatusDescription = "No Content",
            Body = body,
        };
        var beforeStatus = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status);
        Assert.IsNull(ProgressiveDelivery.TryBuild(status, new Request { Method = "GET" }, body, body.Length, false, false));
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status) >= beforeStatus);
    }

    [TestMethod]
    public void SessionRawHttp_InjectsHostFromAbsoluteUrl_WhenHostEmpty()
    {
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://from-url.example:8443/path",
            Host = "  ",
            RequestHeadersText = "Accept: */*\n",
            StatusCode = 302,
            ResponseHeadersText = "Location: /next\r\n",
        };
        using var output = new MemoryStream();
        _ = SessionRawHttp.Write(snap, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "Host: from-url.example");
        StringAssert.Contains(text, "HTTP/1.1 302 Found");
        StringAssert.Contains(text, "GET /path HTTP/1.1");
    }

    [TestMethod]
    public void SessionBodyDiskCache_GetDefaultDirectory_IsUnderLocalAppData()
    {
        var path = SessionBodyDiskCache.GetDefaultDirectory();
        StringAssert.Contains(path, "TitaniumInspector");
        StringAssert.Contains(path, "session-cache");
        Assert.IsTrue(path.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StringComparison.OrdinalIgnoreCase));
    }
}
