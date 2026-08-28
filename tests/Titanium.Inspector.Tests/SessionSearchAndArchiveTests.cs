using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionSearchAndArchiveTests
{
    [TestMethod]
    public void Filter_ByMethodAndIsWs()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/x", IsWebSocket = false },
            new SessionSnapshot { Id = 2, Method = "GET", Url = "https://a/ws", IsWebSocket = true },
            new SessionSnapshot { Id = 3, Method = "POST", Url = "https://a/y", IsWebSocket = false },
        };

        var filtered = SessionSearch.Filter(sessions, "method:GET is:ws").ToList();
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual(2, filtered[0].Id);
    }

    [TestMethod]
    public async Task NativeArchive_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twp-insp-{Guid.NewGuid():N}.zip");
        try
        {
            var sessions = new List<SessionSnapshot>
            {
                new() { Id = 9, Method = "GET", Url = "https://example/" },
            };
            await SessionArchive.ExportNativeArchiveAsync(sessions, path);
            var imported = await SessionArchive.ImportNativeArchiveAsync(path);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual(9, imported[0].Id);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
