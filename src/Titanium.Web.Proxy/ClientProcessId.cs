using System;

namespace Titanium.Web.Proxy;

/// <summary>
///     Capability for resolving the local client process that owns a TCP connection to the proxy.
/// </summary>
public static class ClientProcessId
{
    /// <summary>
    ///     True when this OS can map a localhost client TCP port to a process id
    ///     (Windows, Linux, and macOS).
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
}
