using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Titanium.Web.Proxy.Helpers;

internal partial class NativeMethods
{
    internal const int ProcAllPids = 1;
    internal const int ProcPidListFds = 1;
    internal const int ProcPidFdSocketInfo = 3;
    internal const int ProxFdTypeSocket = 2;
    internal const int SockInfoTcp = 2;
    internal const int DarwinAfInet = 2;
    internal const int DarwinAfInet6 = 30;
    internal const int IpProtoTcp = 6;
    internal const int SocketFdInfoSize = 792;
    internal const int ProcFdInfoSize = 8;

    // Offsets within socket_fdinfo on 64-bit Darwin (verified against sys/proc_info.h).
    internal const int SocketFdInfoOffsetSoiProtocol = 180;
    internal const int SocketFdInfoOffsetSoiFamily = 184;
    internal const int SocketFdInfoOffsetSoiKind = 256;
    internal const int SocketFdInfoOffsetInsiLport = 268;

    [SupportedOSPlatform("macos")]
    [LibraryImport("libproc", EntryPoint = "proc_listpids")]
    internal static partial int ProcListPids(uint type, uint typeInfo, int[]? buffer, int bufferSize);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    internal static partial int ProcPidInfo(int pid, int flavor, ulong arg, byte[]? buffer, int bufferSize);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libproc", EntryPoint = "proc_pidfdinfo")]
    internal static partial int ProcPidFdInfo(int pid, int fd, int flavor, byte[] buffer, int bufferSize);
}
