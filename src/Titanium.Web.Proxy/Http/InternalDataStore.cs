using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http
{
    class InternalDataStore : Dictionary<string, object>
    {
        public bool TryGetValueAs<T>(string key, out T value)
        {
            bool result = TryGetValue(key, out var value1);
            if (result)
            {
                value = (T)value1;
            }
            else
            {
                value = default;
            }

            return result;
        }

        public T GetAs<T>(string key)
        {
            return (T)this[key];
        }
    }
}