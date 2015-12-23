using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Titanium.Web.Http
{

    public class HttpHeader
    {
        public HttpHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) throw new Exception("Name cannot be null");

            Name = name.Trim();
            Value = value.Trim();
        }

        public string Name { get; set; }
        public string Value { get; set; }

        public override string ToString()
        {
            return string.Format("{0}: {1}", Name, Value);
        }
    }
}
