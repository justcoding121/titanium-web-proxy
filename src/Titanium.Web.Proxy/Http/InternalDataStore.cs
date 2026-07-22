using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http;

internal class InternalDataStore : Dictionary<string, object>
{
    public bool TryGetValueAs<T>(string key, out T? value)
    {
        if (TryGetValue(key, out var storedValue))
        {
            value = (T)storedValue;
            return true;
        }

        value = default;
        return false;
    }

    public T GetAs<T>(string key)
    {
        return (T)this[key];
    }
}