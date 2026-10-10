using System.Runtime.InteropServices;

namespace Titanium.Inspector.Localization;

/// <summary>First entry of the macOS preferred-language list (<c>CFLocaleCopyPreferredLanguages</c>).</summary>
internal static partial class MacPreferredLanguage
{
    private const uint Utf8 = 0x08000100;

    public static string? TryGetFirst()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var languages = CFLocaleCopyPreferredLanguages();
        if (languages == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (CFArrayGetCount(languages) < 1)
            {
                return null;
            }

            var value = CFArrayGetValueAtIndex(languages, 0);
            return value == IntPtr.Zero ? null : ReadUtf8(value);
        }
        finally
        {
            CFRelease(languages);
        }
    }

    private static string? ReadUtf8(IntPtr cfString)
    {
        var buffer = new byte[128];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!CFStringGetCString(cfString, handle.AddrOfPinnedObject(), buffer.Length, Utf8))
            {
                return null;
            }
        }
        finally
        {
            handle.Free();
        }

        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial IntPtr CFLocaleCopyPreferredLanguages();

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(IntPtr cf);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr theString, IntPtr buffer, nint bufferSize, uint encoding);
}
