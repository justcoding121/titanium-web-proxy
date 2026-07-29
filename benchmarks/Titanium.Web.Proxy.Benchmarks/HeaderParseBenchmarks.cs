using System.Text;
using BenchmarkDotNet.Attributes;
using Titanium.Web.Proxy.Benchmarks.Support;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Benchmarks;

/// <summary>
///     Measures the real, internal <see cref="HeaderParser.ReadHeaders" /> path used for every
///     request and response header block the proxy reads off the wire. <see cref="HeaderCount" />
///     spans a small request (5), a typical browser request/response (25) and a header-heavy
///     response such as one carrying many <c>Set-Cookie</c> entries (100), so a regression that
///     only shows up at scale is visible without needing a separate load test.
/// </summary>
[MemoryDiagnoser]
public class HeaderParseBenchmarks
{
    [Params(5, 25, 100)]
    public int HeaderCount { get; set; }

    private byte[] headerBlockBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < HeaderCount; i++)
            sb.Append($"X-Benchmark-Header-{i}: value-{i}-0123456789\r\n");
        sb.Append("\r\n");
        headerBlockBytes = Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Benchmark]
    public async System.Threading.Tasks.Task ParseHeaderBlock()
    {
        var bufferPool = new SimpleBufferPool();
        var lineStream = new InMemoryLineStream(headerBlockBytes, bufferPool);
        var headers = new HeaderCollection();
        await HeaderParser.ReadHeaders(lineStream, headers, default);
    }
}
