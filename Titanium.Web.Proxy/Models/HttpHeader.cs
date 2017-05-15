using System;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// Http Header object used by proxy
    /// </summary>
    public class HttpHeader
    {
        public HttpHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new Exception("Name cannot be null");
            }

            Name = name.Trim();
            Value = value.Trim();
        }

        public string Name { get; set; }
        public string Value { get; set; }

        /// <summary>
        /// Returns header as a valid header string
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return string.Format("{0}: {1}", Name, Value);
        }
    }
}