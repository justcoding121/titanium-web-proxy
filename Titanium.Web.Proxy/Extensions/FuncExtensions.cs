using System;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class FuncExtensions
    {
        public static void InvokeParallel<T>(this Func<object, T, Task> callback, object sender, T args)
        {
            Delegate[] invocationList = callback.GetInvocationList();
            Task[] handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = ((Func<object, T, Task>)invocationList[i])(sender, args);
            }

            Task.WhenAll(handlerTasks).Wait();
        }

        public static async Task InvokeParallelAsync<T>(this Func<object, T, Task> callback, object sender, T args)
        {
            Delegate[] invocationList = callback.GetInvocationList();
            Task[] handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = ((Func<object, T, Task>)invocationList[i])(sender, args);
            }

            await Task.WhenAll(handlerTasks);
        }
    }
}
