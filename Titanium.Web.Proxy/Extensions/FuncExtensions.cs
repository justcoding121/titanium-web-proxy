using System;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class FuncExtensions
    {
        public static void InvokeParallel<T>(this AsyncEventHandler<T> callback, object sender, T args)
        {
            var invocationList = callback.GetInvocationList();
            var handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = ((AsyncEventHandler<T>)invocationList[i])(sender, args);
            }

            Task.WhenAll(handlerTasks).Wait();
        }

        public static async Task InvokeParallelAsync<T>(this AsyncEventHandler<T> callback, object sender, T args, Action<Exception> exceptionFunc)
        {
            var invocationList = callback.GetInvocationList();
            var handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = InternalInvokeAsync((AsyncEventHandler<T>)invocationList[i], sender, args, exceptionFunc);
            }

            await Task.WhenAll(handlerTasks);
        }

        public static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args, Action<Exception> exceptionFunc)
        {
            var invocationList = callback.GetInvocationList();

            for (int i = 0; i < invocationList.Length; i++)
            {
                await InternalInvokeAsync((AsyncEventHandler<T>)invocationList[i], sender, args, exceptionFunc);
            }
        }

        private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T args, Action<Exception> exceptionFunc)
        {
            try
            {
                await callback(sender, args);
            }
            catch (Exception ex)
            {
                var ex2 = new Exception("Exception thrown in user event", ex);
                exceptionFunc(ex2);
            }
        }
    }
}
