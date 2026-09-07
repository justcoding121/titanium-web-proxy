using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.AccessLog;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class JsonAccessLogTests
{
    [TestMethod]
    public void FormatRecord_ProducesNdjsonShape()
    {
        var json = JsonAccessLogWriter.FormatRecord(
            method: "GET",
            url: "http://example/x",
            host: "example",
            status: 200,
            durationMs: 12.5,
            clientIp: "127.0.0.1");
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("GET", doc.RootElement.GetProperty("method").GetString());
        Assert.AreEqual(200, doc.RootElement.GetProperty("status").GetInt32());
        Assert.IsFalse(json.Contains("body", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SampleRate_Zero_SkipsWrites()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-access-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            using var writer = new JsonAccessLogWriter(path, sampleRate: 0);
            writer.TryWriteRecord("GET", "http://x/", "x", 200, null, "127.0.0.1");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            Assert.AreEqual("", text.Trim());
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SampleRate_One_AppendsLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-access-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            using (var writer = new JsonAccessLogWriter(path, sampleRate: 1))
            {
                writer.TryWriteRecord("GET", "http://x/access-log", "x", 200, 1.2, "127.0.0.1");
            }

            var text = File.ReadAllText(path);
            Assert.IsTrue(text.Contains("access-log", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForAccessLog()
    {
        var cfg = new TwpConfig
        {
            Server = new ServerConfig
            {
                AccessLog = new AccessLogConfig { Path = "/tmp/access.ndjson", SampleRate = 1 },
            },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void Validator_Rejects_InvalidSampleRate()
    {
        var cfg = new TwpConfig
        {
            Server = new ServerConfig
            {
                AccessLog = new AccessLogConfig { Path = "a.ndjson", SampleRate = 1.5 },
            },
        };
        var errors = TwpConfigValidator.Validate(cfg);
        Assert.IsTrue(errors.Any(e => e.Contains("sampleRate", StringComparison.OrdinalIgnoreCase)));
    }
}
