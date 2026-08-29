using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>Creates the platform <see cref="ISystemProxyBackend"/> when one is available.</summary>
internal static class SystemProxyBackendFactory
{
    [UnsupportedOSPlatform("browser")]
    public static ISystemProxyBackend? Create(IProcessRunner? runner = null, IElevationPrompt? elevation = null)
    {
        if (RunTime.IsWindows && !RunTime.IsUwpOnWindows)
            return new SystemProxyManager();

        if (OperatingSystem.IsMacOS())
            return CreateMac(runner, elevation);

        if (OperatingSystem.IsLinux())
            return CreateLinux(runner);

        return null;
    }

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    private static ISystemProxyBackend CreateMac(IProcessRunner? runner, IElevationPrompt? elevation) =>
        new MacOsSystemProxyBackend(runner, elevation);

    [SupportedOSPlatform("linux")]
    private static ISystemProxyBackend CreateLinux(IProcessRunner? runner) =>
        new LinuxSystemProxyBackend(runner);
}
