using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Manager.Tests;

[TestClass]
public class ManagerDisposeRaceTests
{
    private const string ValidYaml = """
        listeners:
          - port: 8000
            type: explicit
        """;

    [TestMethod]
    public async Task Dispose_WhileStoreRequestsAreInFlight_DoesNotThrow()
    {
        for (var round = 0; round < 12; round++)
        {
            var host = await ManagerTestHost.StartAsync();
            using var admin = host.CreateAdminClient();
            var pending = new List<Task>();
            for (var i = 0; i < 16; i++)
            {
                pending.Add(admin.GetAsync("/v1/fleets"));
            }

            Exception? disposeEx = null;
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception ex)
            {
                disposeEx = ex;
            }
            finally
            {
                foreach (var task in pending)
                {
                    try
                    {
                        await task;
                    }
                    catch (Exception ex) when (ex is HttpRequestException
                        or TaskCanceledException
                        or OperationCanceledException)
                    {
                        // The listener closed under the request. That is the shutdown path.
                    }
                }
            }

            Assert.IsNull(disposeEx, "dispose must finish without throwing while store requests are in flight: "
                + disposeEx);
        }
    }

    [TestMethod]
    public async Task Dispose_WhileLongPollIsWaiting_DoesNotThrow()
    {
        var host = await ManagerTestHost.StartAsync();
        using var admin = host.CreateAdminClient();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "dispose-race" })));
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        using var revDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync($"/v1/fleets/{fleetId}/revisions", ManagerTestHost.Yaml(ValidYaml)));
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();
        await admin.PostAsync($"/v1/fleets/{fleetId}/revisions/{revId}/publish", new StringContent(""));
        var hash = revDoc.RootElement.GetProperty("contentHash").GetString()!;

        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(
            await admin.PostAsync("/v1/agent-tokens", ManagerTestHost.Json(new { fleetId, name = "dispose-race" })));
        var agentToken = tokenDoc.RootElement.GetProperty("token").GetString()!;
        using var agent = host.CreateAgentClient(agentToken);

        var waitTask = agent.GetAsync($"/v1/agent/config?fleet={fleetId}&have={hash}&wait=30");
        var registered = false;
        for (var i = 0; i < 50 && !registered; i++)
        {
            await Task.Delay(20);
            registered = host.WaitHub.CountWaiters(fleetId) >= 1;
        }

        Assert.IsTrue(registered, "long-poll must be in flight before dispose");

        try
        {
            await host.DisposeAsync();
        }
        finally
        {
            try
            {
                await waitTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex) when (ex is HttpRequestException
                or TaskCanceledException
                or OperationCanceledException
                or TimeoutException)
            {
                // The long-poll ends because the host stopped.
            }
        }
    }
}
