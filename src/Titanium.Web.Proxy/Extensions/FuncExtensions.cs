using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.Extensions;

internal static class FuncExtensions
{
    internal static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args,
        ILogger logger)
    {
        var invocationList = callback.GetInvocationList();

        foreach (var @delegate in invocationList)
            await InternalInvokeAsync((AsyncEventHandler<T>)@delegate, sender, args, logger);
    }

    private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T args,
        ILogger logger)
    {
        try
        {
            await callback(sender, args);
        }
        catch (BodySizeLimitExceededException bodyLimitEx)
        {
            // Not a user-handler bug: a handler that calls GetRequestBody()/GetResponseBody() from
            // inside BeforeRequest/BeforeResponse surfaces this the moment MaxBufferedBodyBytes is
            // breached, and it is thrown from *within* the user's own call stack (there is no other
            // vantage point from which the whole-body buffering path can observe the breach). It must
            // propagate to the request/response pipeline so it can produce a 413 (request side) or
            // close the connection (response side) - swallowing it here would let the pipeline carry on
            // as if the body had been read in full.
            ProxyDiagnostics.ReportCaught(logger,
                "User event hit body size limit; rethrowing for pipeline 413/close handling", bodyLimitEx);
            throw;
        }
        catch (Exception e)
        {
            // A user event handler threw: this is always unexpected from the proxy's point of view, so
            // it is reported at Error regardless of what kind of exception it is.
            ProxyDiagnostics.ReportUnexpected(logger, "Exception thrown in user event", e);
        }
    }
}
