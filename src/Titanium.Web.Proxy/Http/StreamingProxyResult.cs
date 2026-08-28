using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Pairs a synthetic response (status and headers) with a delegate that writes the body on the fly.
///     Pass to <see cref="EventArguments.SessionEventArgs.RespondStreaming(StreamingProxyResult, bool)" /> to
///     stream without buffering the whole body in memory.
/// </summary>
/// <param name="Response">Response metadata (status, headers, optional Content-Length).</param>
/// <param name="WriteBody">Delegate invoked to write the body to the client stream.</param>
public readonly record struct StreamingProxyResult(
    Response Response,
    Func<Stream, CancellationToken, Task> WriteBody);
