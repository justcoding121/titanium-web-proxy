using System;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging local/CI test runs.
///     Inspector tests should prefer <c>UseInMemoryTrustState</c>; this is a safety net.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
        Environment.SetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI", "1");
    }
}
