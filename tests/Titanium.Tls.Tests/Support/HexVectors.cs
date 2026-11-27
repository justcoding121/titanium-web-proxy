using System;

namespace Titanium.Tls.Tests.Support;

internal static class HexVectors
{
    internal static byte[] Parse(string text)
    {
        var bytes = new System.Collections.Generic.List<byte>(text.Length / 2);
        int value = -1;
        bool comment = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (comment)
            {
                if (c == '\n')
                {
                    comment = false;
                }

                continue;
            }

            if (c == '#')
            {
                if (value >= 0)
                {
                    throw new FormatException("Hex text has an odd number of digits.");
                }

                comment = true;
                continue;
            }

            if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
            {
                i++;
                continue;
            }

            if (!IsHex(c))
            {
                continue;
            }

            int digit = c <= '9' ? c - '0' : (c & ~32) - 'A' + 10;
            if (value < 0)
            {
                value = digit << 4;
            }
            else
            {
                bytes.Add((byte)(value | digit));
                value = -1;
            }
        }

        if (value >= 0)
        {
            throw new FormatException("Hex text has an odd number of digits.");
        }

        return bytes.ToArray();
    }

    private static bool IsHex(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
