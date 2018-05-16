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

        internal RetryPolicy(int retries, TcpConnectionFactory tcpConnectionFactory)
        {
            this.retries = retries;
            this.tcpConnectionFactory = tcpConnectionFactory;
        }

        //get the policy
        private Policy getRetryPolicy()
        {
            return Policy.Handle<T>()
                .RetryAsync(retries,
                    onRetryAsync: async (ex, i, context) =>
                    {
                        if (context["connection"] != null)
                        {
                            //close connection on error
                            var connection = (TcpServerConnection)context["connection"];
                            await tcpConnectionFactory.Release(connection, true);
                            context["connection"] = null;
                        }

                    });
        }

        /// <summary>
        ///     Execute and retry the given action until retry number of times.
        /// </summary>
        /// <param name="action">The action to retry.</param>
        /// <param name="generator">The Tcp connection generator to be invoked to get new connection for retry.</param>
        /// <param name="initialConnection">Initial Tcp connection to use.</param>
        /// <returns></returns>
        internal Task ExecuteAsync(Func<TcpServerConnection, Task> action,
            Func<Task<TcpServerConnection>> generator, ref TcpServerConnection initialConnection)
        {
            var outerContext = new Dictionary<string, object> { { "connection", initialConnection } };

            Task result;
            try
            {
                result = getRetryPolicy().ExecuteAsync(async (context) =>
                {
                    //setup connection
                    var connection = context["connection"] as TcpServerConnection ??
                                      await generator();

                    context["connection"] = connection;

                    //retry
                    await action(connection);

                }, outerContext);
            }
            //all retries failed
            finally
            {
                //update the original connection to last used connection
                initialConnection = outerContext["connection"] as TcpServerConnection;
            }

            return result;
        }
    }
}
