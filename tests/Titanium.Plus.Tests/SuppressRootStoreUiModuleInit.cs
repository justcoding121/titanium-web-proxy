using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.Plus.Tests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging local/CI test runs.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init() =>
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
}
