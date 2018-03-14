using System;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class FuncExtensions
    {

        public static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args, ExceptionHandler exceptionFunc)
        {
            var invocationList = callback.GetInvocationList();

            for (int i = 0; i < invocationList.Length; i++)
            {
                await InternalInvokeAsync((AsyncEventHandler<T>)invocationList[i], sender, args, exceptionFunc);
            }
        }

        private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T args, ExceptionHandler exceptionFunc)
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
