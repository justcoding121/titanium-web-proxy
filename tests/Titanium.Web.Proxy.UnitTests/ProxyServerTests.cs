using System;
using System.Net;
using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ProxyServerTests
    {
        /// <summary>
        ///     Phase E.14 ("Default to TLS 1.2/1.3 now rather than deferring"): a bare
        ///     <c>new ProxyServer()</c> must negotiate only modern TLS versions; SSL 3.0/TLS 1.0/1.1
        ///     require an explicit opt-in from the caller.
        /// </summary>
        [TestMethod]
        public void DefaultConstructor_SupportedSslProtocols_IsTls12And13Only()
        {
            var proxy = new ProxyServer();

            Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, proxy.SupportedSslProtocols);
#pragma warning disable CS0618, SYSLIB0039 // asserting the legacy protocols are specifically excluded
            Assert.IsFalse(proxy.SupportedSslProtocols.HasFlag(SslProtocols.Ssl3));
            Assert.IsFalse(proxy.SupportedSslProtocols.HasFlag(SslProtocols.Tls));
            Assert.IsFalse(proxy.SupportedSslProtocols.HasFlag(SslProtocols.Tls11));
#pragma warning restore CS0618, SYSLIB0039
        }

        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToAddress_WhenAddingNewEndpointToExistingAddress_ThenExceptionIsThrown()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            var firstIpAddress = IPAddress.Parse("127.0.0.1");
            var secondIpAddress = IPAddress.Parse("127.0.0.1");
            proxy.AddEndPoint(new ExplicitProxyEndPoint(firstIpAddress, port, false));

            // Act
            try
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(secondIpAddress, port, false));
            }
            catch (Exception exc)
            {
                // Assert
                StringAssert.Contains(exc.Message, "Cannot add another endpoint to same port");
                return;
            }

            Assert.Fail("An exception should be thrown by now");
        }

        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToAddress_WhenAddingNewEndpointToExistingAddress_ThenTwoEndpointsExists()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            var firstIpAddress = IPAddress.Parse("127.0.0.1");
            var secondIpAddress = IPAddress.Parse("192.168.1.1");
            proxy.AddEndPoint(new ExplicitProxyEndPoint(firstIpAddress, port, false));

            // Act
            proxy.AddEndPoint(new ExplicitProxyEndPoint(secondIpAddress, port, false));

            // Assert
            Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        }

        [TestMethod]
        public void GivenOneEndpointIsAlreadyAddedToPort_WhenAddingNewEndpointToExistingPort_ThenExceptionIsThrown()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 9999;
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Act
            try
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));
            }
            catch (Exception exc)
            {
                // Assert
                StringAssert.Contains(exc.Message, "Cannot add another endpoint to same port");
                return;
            }

            Assert.Fail("An exception should be thrown by now");
        }

        [TestMethod]
        public void
            GivenOneEndpointIsAlreadyAddedToZeroPort_WhenAddingNewEndpointToExistingPort_ThenTwoEndpointsExists()
        {
            // Arrange
            var proxy = new ProxyServer();
            const int port = 0;
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Act
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, port, false));

            // Assert
            Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        }

        [TestMethod]
        public void EnableHttp2_DefaultsToTrue_AfterFullHttp2QualificationPass()
        {
            var proxy = new ProxyServer();

            Assert.IsTrue(proxy.EnableHttp2);
        }

        /// <summary>
        ///     Phase F.16: a fresh <c>new ProxyServer()</c> must report <see cref="ProxyProfile.Balanced" />
        ///     and <see cref="ProxyPolicyModes.AllEnforce" /> without the <see cref="ProxyServer.Profile" />
        ///     setter ever having run, since every field it would set already starts at that value.
        /// </summary>
        [TestMethod]
        public void DefaultConstructor_Profile_IsBalancedWithAllPolicyFamiliesEnforced()
        {
            var proxy = new ProxyServer();

            Assert.AreEqual(ProxyProfile.Balanced, proxy.Profile);
            Assert.AreEqual(PolicyMode.Enforce, proxy.PolicyModes[PolicyFamily.BodyBudget]);
            Assert.AreEqual(PolicyMode.Enforce, proxy.PolicyModes[PolicyFamily.AdmissionControl]);
            Assert.IsFalse(proxy.PolicyModes.AllowAmbiguousFraming);
            Assert.IsFalse(proxy.BlockPrivateNetworkDestinations);
        }

        [TestMethod]
        public void SettingProfile_ToPublicFacing_AppliesTheWholeBundleAtomically()
        {
            var proxy = new ProxyServer { Profile = ProxyProfile.PublicFacing };

            Assert.AreEqual(ProxyProfile.PublicFacing, proxy.Profile);
            Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
            Assert.AreEqual(10_000, proxy.MaxConcurrentClientConnections);
            Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, proxy.SupportedSslProtocols);
            Assert.AreEqual(PolicyMode.Enforce, proxy.PolicyModes[PolicyFamily.AdmissionControl]);
            Assert.IsTrue(proxy.ClientHeaderTimeoutSeconds > 0);
        }

        [TestMethod]
        public void SettingProfile_ToLegacyCompatible_ThenBack_ToBalanced_RestoresAllEnforce()
        {
            var proxy = new ProxyServer { Profile = ProxyProfile.LegacyCompatible };
            Assert.AreEqual(PolicyMode.Observe, proxy.PolicyModes[PolicyFamily.AdmissionControl]);

            proxy.Profile = ProxyProfile.Balanced;

            Assert.AreEqual(PolicyMode.Enforce, proxy.PolicyModes[PolicyFamily.AdmissionControl]);
            Assert.IsFalse(proxy.BlockPrivateNetworkDestinations);
        }

        [TestMethod]
        public void AssigningPolicyModes_AfterSelectingAProfile_OverridesOnlyPolicyModes()
        {
            var proxy = new ProxyServer { Profile = ProxyProfile.PublicFacing };

            proxy.PolicyModes = ProxyPolicyModes.AllEnforce.With(PolicyFamily.AdmissionControl, PolicyMode.Observe);

            Assert.AreEqual(PolicyMode.Observe, proxy.PolicyModes[PolicyFamily.AdmissionControl]);
            // The rest of the PublicFacing bundle (unrelated to PolicyModes) must remain intact.
            Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
            Assert.AreEqual(10_000, proxy.MaxConcurrentClientConnections);
        }

        [TestMethod]
        public void PolicyModes_Setter_RejectsNull()
        {
            var proxy = new ProxyServer();

            Assert.ThrowsExactly<ArgumentNullException>(() => proxy.PolicyModes = null!);
        }
    }
}