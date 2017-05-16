﻿using System;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extension methods for Byte Arrays.
    /// </summary>
    internal static class ByteArrayExtensions
    {
        /// <summary>
        /// Get the sub array from byte of data
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="index"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        internal static T[] SubArray<T>(this T[] data, int index, int length)
        {
            var result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }
    }
}
