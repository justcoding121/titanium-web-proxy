using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Protobuf wire-format decode via ViewModel inspect pane (no system proxy).</summary>
public static class ProtobufDecodeScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            // field 1 string "probe-pb"
            var payload = new byte[]
            {
                0x0a, 0x08,
                (byte)'p', (byte)'r', (byte)'o', (byte)'b', (byte)'e', (byte)'-', (byte)'p', (byte)'b',
            };
            var framed = new byte[5 + payload.Length];
            framed[4] = (byte)payload.Length;
            payload.CopyTo(framed.AsSpan(5));

            var decoded = ProtobufMessageDecoder.DecodeWireFormat(framed);
            if (!decoded.Contains("probe-pb", StringComparison.Ordinal) ||
                !decoded.Contains("\"field\": 1", StringComparison.Ordinal))
            {
                log.Step("protobuf-decode", false, "wire decode failed");
                return 1;
            }

            await harness.OnUiAsync(() =>
            {
                var snap = new SessionSnapshot
                {
                    Id = 9101,
                    Method = "POST",
                    Url = "https://probe.local/grpc.Service/Method",
                    Host = "probe.local",
                    StatusCode = 200,
                    IsGrpc = true,
                    ContentType = "application/grpc",
                    ResponseBodyBytes = framed,
                    ProtobufDecodedText = decoded,
                };
                harness.ViewModel.SeedSession(snap);
                harness.ViewModel.SelectedSession = snap;
            }).ConfigureAwait(true);

            var show = false;
            var text = "";
            await harness.OnUiAsync(() =>
            {
                show = harness.ViewModel.ShowProtobufTab;
                text = harness.ViewModel.SelectedProtobufDecoded;
                if (show)
                {
                    harness.Robot.Click("TabProtobuf");
                }
            }).ConfigureAwait(true);

            var ok = show && text.Contains("probe-pb", StringComparison.Ordinal);
            log.Step("protobuf-decode", ok, $"show={show} textLen={text.Length}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("protobuf-decode", false, ex.Message);
            return 1;
        }
    }
}
