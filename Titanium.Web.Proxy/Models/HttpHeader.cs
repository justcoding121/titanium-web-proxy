﻿using System;
using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// Http Header object used by proxy
    /// </summary>
    public class HttpHeader
    {
        internal static Version Version10 = new Version(1, 0);

        internal static Version Version11 = new Version(1, 1);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <exception cref="Exception"></exception>
        public HttpHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new Exception("Name cannot be null");
            }

            Name = name.Trim();
            Value = value.Trim();
        }

        /// <summary>
        /// Header Name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Header Value.
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Returns header as a valid header string
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return $"{Name}: {Value}";
        }

        internal async Task WriteToStream(StreamWriter writer)
        {
            await writer.WriteAsync(Name);
            await writer.WriteAsync(": ");
            await writer.WriteLineAsync(Value);
        }
    }
}
