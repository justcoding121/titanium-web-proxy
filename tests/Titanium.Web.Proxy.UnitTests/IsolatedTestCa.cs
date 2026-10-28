using System;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unique root-CA subject for tests that create a <c>CertificateManager</c> root in memory.
///     The product default <c>Titanium Root Certificate Authority</c> is routinely present in
///     <c>CurrentUser\Root</c> on developer machines (Inspector, examples, earlier runs). On Windows a
///     second, different certificate with the same subject makes <c>X509Chain.Build</c> throw
///     "An unknown chain building error occurred", which turned local runs red while CI (clean runner)
///     stayed green. Never let a test mint a root with the default subject.
/// </summary>
internal static class IsolatedTestCa
{
    internal const string Issuer = "TitaniumUnitTest";

    internal static string NewRootName() => "Titanium UnitTest Root CA " + Guid.NewGuid().ToString("N");
}
