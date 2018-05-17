using Polly;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Network
{
    internal class RetryPolicy<T> where T : Exception
    {
        private readonly int retries;
        private readonly TcpConnectionFactory tcpConnectionFactory;

        private TcpServerConnection currentConnection;
        private Exception exception;

        internal RetryPolicy(int retries, TcpConnectionFactory tcpConnectionFactory)
        {
            this.retries = retries;
            this.tcpConnectionFactory = tcpConnectionFactory;
        }

        /// <summary>
        ///     Execute and retry the given action until retry number of times.
        /// </summary>
        /// <param name="action">The action to retry.</param>
        /// <param name="generator">The Tcp connection generator to be invoked to get new connection for retry.</param>
        /// <param name="initialConnection">Initial Tcp connection to use.</param>
        /// <returns>Returns the latest connection used and the latest exception if any.</returns>
        internal async Task<RetryResult> ExecuteAsync(Func<TcpServerConnection, Task> action,
            Func<Task<TcpServerConnection>> generator, TcpServerConnection initialConnection)
        {
            currentConnection = initialConnection;
            try
            {
                //retry on error with polly policy
                //do not use polly context to store connection; it does not save states b/w attempts
                await getRetryPolicy().ExecuteAsync(async () =>
                {
                    //setup connection
                    currentConnection = currentConnection as TcpServerConnection ??
                                      await generator();
                    //try
                    await action(currentConnection);

                });
            }
            catch (Exception e) { exception = e; }

            return new RetryResult(currentConnection, exception);
        }

        //get the policy
        private Policy getRetryPolicy()
        {
            return Policy.Handle<T>()
                .RetryAsync(retries,
                    onRetryAsync: async (ex, i, context) =>
                    {
                        if (currentConnection != null)
                        {
                            //close connection on error
                            await tcpConnectionFactory.Release(currentConnection, true);
                            currentConnection = null;
                        }

                    });
        }
    }

    internal class RetryResult
    {
        internal bool IsSuccess => Exception == null;
        internal TcpServerConnection LatestConnection { get; }
        internal Exception Exception { get; }

        internal RetryResult(TcpServerConnection lastConnection, Exception exception)
        {
            LatestConnection = lastConnection;
            Exception = exception;
        }
    }
}
