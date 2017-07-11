using System;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class FuncExtensions
    {
        public static void InvokeParallel<T>(this Func<object, T, Task> callback, object sender, T args)
        {
            var invocationList = callback.GetInvocationList();
            var handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = ((Func<object, T, Task>)invocationList[i])(sender, args);
            }

            Task.WhenAll(handlerTasks).Wait();
        }

        public static async Task InvokeParallelAsync<T>(this Func<object, T, Task> callback, object sender, T args, Action<Exception> exceptionFunc)
        {
            var invocationList = callback.GetInvocationList();
            var handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = InvokeAsync((Func<object, T, Task>)invocationList[i], sender, args, exceptionFunc);
            }

            await Task.WhenAll(handlerTasks);
        }

        private static async Task InvokeAsync<T>(Func<object, T, Task> callback, object sender, T args, Action<Exception> exceptionFunc)
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
