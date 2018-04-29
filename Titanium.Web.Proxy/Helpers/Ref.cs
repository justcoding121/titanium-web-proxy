using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    internal class Ref<T>
    {
        internal Ref()
        {
        }

        internal Ref(T value)
        {
            Value = value;
        }

        internal T Value { get; set; }

        public override string ToString()
        {
            T value = Value;
            return value == null ? string.Empty : value.ToString();
        }

        public static implicit operator T(Ref<T> r)
        {
            return r.Value;
        }

        public static implicit operator Ref<T>(T value)
        {
            return new Ref<T>(value);
        }
    }
}
