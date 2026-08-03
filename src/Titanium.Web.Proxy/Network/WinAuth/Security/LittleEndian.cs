//
// Mono.Security.BitConverterLE.cs
//  Like System.BitConverter but always little endian
//
// Author:
//   Bernie Solomon
//
// Rewritten without unsafe code for Sonar S6640.
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Buffers.Binary;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

internal sealed class LittleEndian
{
    private LittleEndian()
    {
    }

    internal static byte[] GetBytes(bool value)
    {
        return new[] { value ? (byte)1 : (byte)0 };
    }

    internal static byte[] GetBytes(char value) => GetBytes((ushort)value);

    internal static byte[] GetBytes(short value)
    {
        var bytes = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(ushort value)
    {
        var bytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(float value)
    {
        var bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] GetBytes(double value)
    {
        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    internal static bool ToBoolean(byte[] value, int startIndex)
    {
        return value[startIndex] != 0;
    }

    internal static char ToChar(byte[] value, int startIndex) => (char)ToUInt16(value, startIndex);

    internal static short ToInt16(byte[] value, int startIndex)
        => BinaryPrimitives.ReadInt16LittleEndian(value.AsSpan(startIndex));

    internal static int ToInt32(byte[] value, int startIndex)
        => BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(startIndex));

    internal static long ToInt64(byte[] value, int startIndex)
        => BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(startIndex));

    internal static ushort ToUInt16(byte[] value, int startIndex)
        => BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(startIndex));

    internal static uint ToUInt32(byte[] value, int startIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(startIndex));

    internal static ulong ToUInt64(byte[] value, int startIndex)
        => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(startIndex));

    internal static float ToSingle(byte[] value, int startIndex)
        => BinaryPrimitives.ReadSingleLittleEndian(value.AsSpan(startIndex));

    internal static double ToDouble(byte[] value, int startIndex)
        => BinaryPrimitives.ReadDoubleLittleEndian(value.AsSpan(startIndex));
}
