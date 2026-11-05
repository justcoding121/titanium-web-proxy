using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.TestKit.Shared;

/// <summary>
///     Stops Windows CryptUI, macOS Keychain, and Linux polkit root-store dialogs from hanging
///     a local or CI test run. Compiled into every test assembly. The environment variable stays
///     set even if a test clears <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/>.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
        Environment.SetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI", "1");
        // The policy stays internal. Every test host, including assemblies without InternalsVisibleTo, registers here.
        var policy = typeof(CertificateManager).Assembly.GetType("Titanium.Web.Proxy.Network.Quic.QuicListenPolicy")
            ?? throw new InvalidOperationException("QuicListenPolicy is missing.");
        var beforeListen = policy.GetProperty("BeforeListen", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuicListenPolicy.BeforeListen is missing.");
        beforeListen.SetValue(null, (Action)(() =>
            Assert.Inconclusive(
                "MsQuic reports a loopback QUIC listen as a wildcard UDP socket. " +
                "Set TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1 to run this test where a firewall prompt will not appear.")));
    }
}
