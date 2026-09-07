using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MapLocalIntegrationTests
{
    [TestMethod]
    public async Task MapLocal_ViaInterceptionService_ReturnsFileBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-map-local-int-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, [0x01, 0x02, 0xFF, 0x00]);
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            interception.AutoResponder = new AutoResponderViewModel { Enabled = true };
            interception.AutoResponder.Rules.Add(new AutoResponderRule
            {
                MatchUrl = "*map-int*",
                StatusCode = 200,
                ContentType = "application/octet-stream",
                LocalFilePath = path,
                Enabled = true,
            });

            await interception.StartAsync(IPAddress.Loopback, 0);
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync("http://127.0.0.1:9/map-int");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0xFF, 0x00 }, await response.Content.ReadAsByteArrayAsync());
            interception.Stop();
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
