using System;
using System.Runtime.InteropServices;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers.WinHttp;

internal static class NativeMethods
{
    internal static class WinHttp
    {
        [DllImport("winhttp.dll", SetLastError = true)]
        internal static extern bool WinHttpGetIEProxyConfigForCurrentUser( // NOSONAR SYSLIB1054 -- Legacy marshalling is required by this existing interop signature.
            ref WinhttpCurrentUserIeProxyConfig proxyConfig);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern WinHttpHandle WinHttpOpen(string? userAgent, AccessType accessType, string? proxyName, // NOSONAR SYSLIB1054 -- Legacy marshalling is required by this existing interop signature.
            string? proxyBypass, int dwFlags);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpSetTimeouts(WinHttpHandle session, int resolveTimeout, // NOSONAR SYSLIB1054 -- Legacy marshalling is required by this existing interop signature.
            int connectTimeout, int sendTimeout, int receiveTimeout);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpGetProxyForUrl(WinHttpHandle session, string url, // NOSONAR SYSLIB1054 -- Legacy marshalling is required by this existing interop signature.
            [In] ref WinhttpAutoproxyOptions autoProxyOptions,
            out WinhttpProxyInfo proxyInfo);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpCloseHandle(IntPtr httpSession); // NOSONAR SYSLIB1054 -- Legacy marshalling is required by this existing interop signature.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpCurrentUserIeProxyConfig
        {
            public bool AutoDetect;
            public IntPtr AutoConfigUrl;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        [Flags]
        internal enum AutoProxyFlags // NOSONAR S2344 -- Name mirrors the native WINHTTP_AUTOPROXY_OPTIONS flags.
        {
            AutoDetect = 1,
            AutoProxyConfigUrl = 2,
            RunInProcess = 65536,
            RunOutProcessOnly = 131072
        }

        internal enum AccessType
        {
            DefaultProxy = 0,
            NoProxy = 1,
            NamedProxy = 3
        }

        [Flags]
        internal enum AutoDetectType // NOSONAR S2342 -- Name mirrors the native WinHTTP enum.
        {
            None = 0,
            Dhcp = 1,
            DnsA = 2
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpAutoproxyOptions
        {
            public AutoProxyFlags Flags;
            public AutoDetectType AutoDetectFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string? AutoConfigUrl;
            private readonly IntPtr lpvReserved;
            private readonly int dwReserved;
            public bool AutoLogonIfChallenged;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpProxyInfo
        {
            public AccessType AccessType;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        internal enum ErrorCodes
        {
            Success = 0,
            OutOfHandles = 12001,
            Timeout = 12002,
            InternalError = 12004,
            InvalidUrl = 12005,
            UnrecognizedScheme = 12006,
            NameNotResolved = 12007,
            InvalidOption = 12009,
            OptionNotSettable = 12011,
            Shutdown = 12012,
            LoginFailure = 12015,
            OperationCancelled = 12017,
            IncorrectHandleType = 12018,
            IncorrectHandleState = 12019,
            CannotConnect = 12029,
            ConnectionError = 12030,
            ResendRequest = 12032,
            SecureCertDateInvalid = 12037,
            SecureCertCnInvalid = 12038,
            AuthCertNeeded = 12044,
            SecureInvalidCa = 12045,
            SecureCertRevFailed = 12057,
            CannotCallBeforeOpen = 12100,
            CannotCallBeforeSend = 12101,
            CannotCallAfterSend = 12102,
            CannotCallAfterOpen = 12103,
            HeaderNotFound = 12150,
            InvalidServerResponse = 12152,
            InvalidHeader = 12153,
            InvalidQueryRequest = 12154,
            HeaderAlreadyExists = 12155,
            RedirectFailed = 12156,
            SecureChannelError = 12157,
            BadAutoProxyScript = 12166,
            UnableToDownloadScript = 12167,
            SecureInvalidCert = 12169,
            SecureCertRevoked = 12170,
            NotInitialized = 12172,
            SecureFailure = 12175,
            AutoProxyServiceError = 12178,
            SecureCertWrongUsage = 12179,
            AudodetectionFailed = 12180,
            HeaderCountExceeded = 12181,
            HeaderSizeOverflow = 12182,
            ChunkedEncodingHeaderSizeOverflow = 12183,
            ResponseDrainOverflow = 12184,
            ClientCertNoPrivateKey = 12185,
            ClientCertNoAccessPrivateKey = 12186
        }
    }
}
