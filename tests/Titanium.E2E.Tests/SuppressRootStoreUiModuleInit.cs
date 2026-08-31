using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
///     Prevent Windows CryptUI Root Store Yes/No dialogs from hanging automated E2E.
///     Intentional interactive Install CA (e.g. E2E-Slow Chrome) must set
///     <see cref="CertificateManager.SuppressInteractiveRootStoreMutations"/> to false.
/// </summary>
internal static class SuppressRootStoreUiModuleInit
{
    [ModuleInitializer]
    internal static void Init() =>
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
}
