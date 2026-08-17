using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.Extensions;

internal static class FuncExtensions
{
    internal static Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args,
        ILogger logger)
    {
        var invocationList = callback.GetInvocationList();

        // Single subscriber is the common case — avoid GetInvocationList allocation churn is already
        // paid, but skip an outer async state machine when the handler's Task completed inline.
        if (invocationList.Length == 1)
            return InvokeOneAsync((AsyncEventHandler<T>)invocationList[0], sender, args, logger);

        return InvokeManyAsync(invocationList, sender, args, logger);
    }

    private static async Task InvokeManyAsync<T>(Delegate[] invocationList, object sender, T args,
        ILogger logger)
    {
        foreach (var @delegate in invocationList)
            await InvokeOneAsync((AsyncEventHandler<T>)@delegate, sender, args, logger);
    }

    private static Task InvokeOneAsync<T>(AsyncEventHandler<T> callback, object sender, T args,
        ILogger logger)
    {
        Task task;
        try
        {
            task = callback(sender, args);
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
            return Task.CompletedTask;
        }

        if (task.IsCompletedSuccessfully)
            return Task.CompletedTask;

        return ObserveAsync(task, logger);
    }

    private static async Task ObserveAsync(Task task, ILogger logger)
    {
        try
        {
            await task;
        }
        catch (BodySizeLimitExceededException bodyLimitEx)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "User event hit body size limit; rethrowing for pipeline 413/close handling", bodyLimitEx);
            throw;
        }
        catch (Exception e)
        {
            ProxyDiagnostics.ReportUnexpected(logger, "Exception thrown in user event", e);
        }
    }
}
