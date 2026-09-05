using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging automated E2E.
///     Does <b>not</b> set <c>TITANIUM_SKIP_ROOT_STORE_UI</c> so intentional interactive Install CA
///     (local <c>E2E-Slow</c>) can opt in by setting
///     <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/> to false — unless CI /
///     <c>TITANIUM_SKIP_ROOT_STORE_UI=1</c> is already set (those always win).
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init() =>
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
}
