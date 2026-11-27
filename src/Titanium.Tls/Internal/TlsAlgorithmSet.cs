namespace Titanium.Tls;

internal static class TlsAlgorithmSet
{
    internal static bool AllowsCipher(TlsAlgorithmPolicy policy, ushort cipher)
    {
        if (policy != TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return true;
        }

        return cipher is 0x1301 or 0x1302;
    }

    internal static bool AllowsGroup(TlsAlgorithmPolicy policy, ushort group)
    {
        if (policy != TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return true;
        }

        return group is 0x0017 or 0x0018;
    }

    internal static bool AllowsSignature(TlsAlgorithmPolicy policy, ushort scheme)
    {
        if (policy != TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return true;
        }

        return scheme is 0x0403 or 0x0503 or 0x0804 or 0x0805 or 0x0806 or 0x0809 or 0x080A or 0x080B;
    }

    internal static bool AllowsRsa(TlsAlgorithmPolicy policy, int bits)
    {
        return policy != TlsAlgorithmPolicy.FipsApprovedOnly || bits >= 2048;
    }

    internal static bool IsApprovedOffer(ushort value)
    {
        return AllowsCipher(TlsAlgorithmPolicy.FipsApprovedOnly, value) ||
            AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, value) ||
            AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, value);
    }
}
