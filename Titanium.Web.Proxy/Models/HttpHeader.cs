using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Titanium.Web.Proxy.Models
{
    public class HttpHeader
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public HttpHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) throw new Exception("Name or value cannot be null");

            this.Name = name.Trim();
            this.Value = value.Trim();
        }
        public override string ToString()
        {
            return String.Format("{0}: {1}", this.Name, this.Value);
        }
    }
}
