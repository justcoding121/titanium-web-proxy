// Autobahn|Testsuite client-mode runner: connects to a fuzzingserver (through the proxy under test) and echoes every
// message back, once per case. The server judges what it receives and writes the report.
//
//   dotnet run tests/conformance/autobahn/AutobahnClient.cs -- ws://127.0.0.1:9101 titanium [maxCases]
//
// This is a file-based app (needs the .NET 10 SDK); there is no project to build. Scope: it exercises the proxy's
// WebSocket relay with the frames a fuzzingserver sends. Cases that System.Net.WebSockets cannot take part in
// (it validates UTF-8 and close codes itself) belong in tests/conformance/autobahn-known.txt.
using System.Net.WebSockets;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: AutobahnClient.cs <ws://host:port> <agent> [maxCases]");
    return 2;
}

var baseUri = args[0].TrimEnd('/');
var agent = Uri.EscapeDataString(args[1]);
var maxCases = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;
using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(90));

var count = int.Parse(await ReadFirstTextAsync($"{baseUri}/getCaseCount", overall.Token));
count = Math.Min(count, maxCases);
Console.WriteLine($"Autobahn cases: {count}");

for (var n = 1; n <= count; n++)
{
    using var perCase = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
    perCase.CancelAfter(TimeSpan.FromSeconds(60));
    try
    {
        await EchoAsync($"{baseUri}/runCase?case={n}&agent={agent}", perCase.Token);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
    {
        // The server records its own verdict; a client that cannot follow a case just moves on.
        Console.WriteLine($"case {n}: {ex.GetType().Name}: {ex.Message}");
    }
}

try
{
    using var update = new ClientWebSocket();
    await update.ConnectAsync(new Uri($"{baseUri}/updateReports?agent={agent}"), overall.Token);
    await DrainAsync(update, overall.Token);
}
catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
{
    Console.Error.WriteLine($"updateReports: {ex.Message}");
    return 1;
}

return 0;

static async Task<string> ReadFirstTextAsync(string url, CancellationToken ct)
{
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(url), ct);
    var (type, data) = await ReceiveMessageAsync(ws, ct);
    if (type != WebSocketMessageType.Text)
        throw new InvalidOperationException("expected a text message");
    await DrainAsync(ws, ct);
    return Encoding.UTF8.GetString(data);
}

static async Task EchoAsync(string url, CancellationToken ct)
{
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(url), ct);
    while (ws.State == WebSocketState.Open)
    {
        var (type, data) = await ReceiveMessageAsync(ws, ct);
        if (type == WebSocketMessageType.Close)
            break;
        await ws.SendAsync(data, type, true, ct);
    }

    if (ws.State == WebSocketState.CloseReceived)
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
}

static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
{
    var buffer = new byte[16 * 1024];
    using var message = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return (WebSocketMessageType.Close, []);
        message.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            return (result.MessageType, message.ToArray());
    }
}

static async Task DrainAsync(ClientWebSocket ws, CancellationToken ct)
{
    while (ws.State is WebSocketState.Open)
    {
        var (type, _) = await ReceiveMessageAsync(ws, ct);
        if (type == WebSocketMessageType.Close)
            break;
    }

    if (ws.State == WebSocketState.CloseReceived)
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
}
