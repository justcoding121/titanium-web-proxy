using System;

namespace Titanium.Web.Proxy.Extensions
{
    public static class ByteArrayExtensions
    {
        /// <summary>
        /// Get the sub array from byte of data
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="index"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static T[] SubArray<T>(this T[] data, int index, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }
       
    }
}
