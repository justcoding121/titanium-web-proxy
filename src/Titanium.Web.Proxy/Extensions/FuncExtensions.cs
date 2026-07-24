using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;

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
        catch (Exception e)
        {
            // A user event handler threw: this is always unexpected from the proxy's point of view, so
            // it is reported at Error regardless of what kind of exception it is.
            ProxyDiagnostics.ReportUnexpected(logger, "Exception thrown in user event", e);
        }
    }
}
