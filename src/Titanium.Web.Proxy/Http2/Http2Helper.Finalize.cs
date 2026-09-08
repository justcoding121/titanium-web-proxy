using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    internal partial class Http2Helper
    {
        private const string AfterResponseFailedMessage = "HTTP/2 AfterResponse handler failed";

        internal static async Task FinalizeStreamAsync(Http2StreamState state,
            Func<SessionEventArgs, Task> onAfterResponse, ILogger logger,
            Http2ConnectionState? connectionState = null)
        {
            if (Interlocked.CompareExchange(ref state.FinalizedFlag, 1, 0) != 0)
            {
                return;
            }

            // Compressed-relay streams never allocate SessionEventArgs. CTS is TryReset in PrepareForPool.
            if (state.SessionArgs == null)
            {
                connectionState?.ReturnStreamState(state);
                return;
            }

            try
            {
                var requestDispatch = state.SessionArgs.HttpClient.Request.Http2BeforeHandlerTask;
                var responseDispatch = state.SessionArgs.HttpClient.Response.Http2BeforeHandlerTask;
                if (requestDispatch != null)
                    await requestDispatch;
                if (responseDispatch != null && !ReferenceEquals(responseDispatch, requestDispatch))
                    await responseDispatch;

                await onAfterResponse(state.SessionArgs);
            }
            catch (Exception ex)
            {
                ReportException(logger, new ProxyHttpException(AfterResponseFailedMessage, ex,
                    state.SessionArgs));
            }
            finally
            {
                state.SessionArgs.Dispose();
                connectionState?.ReturnStreamState(state);
            }
        }

        /// <summary>
        ///     Schedules finalize. Compressed-relay finalize is synchronous when AfterResponse completes
        ///     inline (gate-off has no SessionEventArgs; MITM unchanged-lite usually Completes Task) —
        ///     avoid allocating a <see cref="Task"/> into <see cref="Http2ConnectionState.PendingFinalizations"/>.
        /// </summary>
        private static void ScheduleFinalize(Http2StreamState state,
            Func<SessionEventArgs, Task> onAfterResponse, ILogger logger,
            Http2ConnectionState connectionState)
        {
            if (state.IsCompressedRelay)
            {
                // Inline hot path: FinalizedFlag + optional sync AfterResponse/Dispose + pool return.
                if (Interlocked.CompareExchange(ref state.FinalizedFlag, 1, 0) != 0)
                    return;

                var args = state.SessionArgs;
                if (args == null)
                {
                    connectionState.ReturnStreamState(state);
                    return;
                }

                // MITM unchanged-lite: both Before* dispatches finished before END_STREAM closed the
                // stream. Prefer sync AfterResponse+Dispose; async AfterResponse falls back to Task track.
                try
                {
                    var after = onAfterResponse(args);
                    if (after.IsCompletedSuccessfully)
                    {
                        args.Dispose();
                        connectionState.ReturnStreamState(state);
                        return;
                    }

                    if (after.IsCompleted)
                    {
                        // Faulted/canceled CompletedTask — still dispose; report like FinalizeStreamAsync.
                        if (after.IsFaulted)
                        {
                            ReportException(logger, new ProxyHttpException(AfterResponseFailedMessage,
                                after.Exception?.GetBaseException(), args));
                        }

                        args.Dispose();
                        connectionState.ReturnStreamState(state);
                        return;
                    }

                    // Rare async AfterResponse: finish on the pending bag (FinalizedFlag already set).
                    connectionState.PendingFinalizations.Track(CompleteMitmCompressedFinalizeAsync(
                        after, args, state, logger, connectionState));
                    return;
                }
                catch (Exception ex)
                {
                    ReportException(logger, new ProxyHttpException(AfterResponseFailedMessage, ex, args));
                    args.Dispose(); // NOSONAR S3966 -- Catch path after onAfterResponse threw; try returns before Dispose.
                    connectionState.ReturnStreamState(state);
                    return;
                }
            }

            connectionState.PendingFinalizations.Track(
                FinalizeStreamAsync(state, onAfterResponse, logger, connectionState));
        }

        private static async Task CompleteMitmCompressedFinalizeAsync(Task afterResponse,
            SessionEventArgs args, Http2StreamState state, ILogger logger,
            Http2ConnectionState connectionState)
        {
            try
            {
                await afterResponse.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportException(logger, new ProxyHttpException(AfterResponseFailedMessage, ex, args));
            }
            finally
            {
                args.Dispose();
                connectionState.ReturnStreamState(state);
            }
        }
    }
}
