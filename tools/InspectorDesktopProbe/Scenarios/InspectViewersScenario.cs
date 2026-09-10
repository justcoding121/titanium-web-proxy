namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Aggregate of protobuf + SSE + throttle (kept for backward-compatible probe command).</summary>
public static class InspectViewersScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        var codes = new[]
        {
            await ProtobufDecodeScenario.RunAsync(harness, log).ConfigureAwait(true),
            await SseViewerScenario.RunAsync(harness, log).ConfigureAwait(true),
            await NetworkThrottleScenario.RunAsync(harness, log).ConfigureAwait(true),
        };
        var ok = codes.All(c => c == 0);
        log.Step("inspect-viewers", ok, string.Join(",", codes));
        return ok ? 0 : 1;
    }
}
