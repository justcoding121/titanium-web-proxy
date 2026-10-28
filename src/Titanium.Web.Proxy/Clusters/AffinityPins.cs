using System;
using System.Security.Cryptography;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Opaque sticky pin. Never the raw destination id unless the destination sets one.</summary>
internal static class AffinityPins
{
    public static string For(ClusterConfig cluster, DestinationConfig destination)
    {
        if (!string.IsNullOrEmpty(destination.AffinityKey))
        {
            return destination.AffinityKey;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cluster.Id + ":" + destination.Id));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    public static bool Matches(ClusterConfig cluster, DestinationConfig destination, string key) =>
        string.Equals(destination.Id, key, StringComparison.Ordinal) ||
        string.Equals(destination.AffinityKey, key, StringComparison.Ordinal) ||
        string.Equals(For(cluster, destination), key, StringComparison.Ordinal);
}
