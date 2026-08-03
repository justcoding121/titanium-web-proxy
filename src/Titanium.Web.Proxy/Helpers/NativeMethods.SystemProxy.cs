using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers;

internal partial class NativeMethods
{
    // WinINet exports A/W variants only; DllImport used to append the suffix automatically.
    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
        int dwBufferLength);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(ConsoleEventDelegate callback,
        [MarshalAs(UnmanagedType.Bool)] bool add);

    /// <summary>
    ///     <see href="https://docs.microsoft.com/en-us/windows/desktop/api/winuser/nf-winuser-getsystemmetrics" />
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    // Pinvoke
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate bool ConsoleEventDelegate(int eventType);
}
