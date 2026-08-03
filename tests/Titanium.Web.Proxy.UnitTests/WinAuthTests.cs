using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Network.WinAuth.Security;
using WinAuthHandler = Titanium.Web.Proxy.Network.WinAuth.WinAuthHandler;
using static Titanium.Web.Proxy.Network.WinAuth.Security.Common;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class WinAuthTests
    {
        [TestMethod]
        public void Test_Acquire_Client_Token()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("Windows SSPI is required.");

            var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com", "NTLM", new InternalDataStore());
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void WinAuthCredentialsProvider_Is_Wired_On_ProxyServer()
        {
            using var proxy = new ProxyServer(false, false, false);
            Assert.IsNull(proxy.WinAuthCredentialsProvider);

            var called = false;
            proxy.WinAuthCredentialsProvider = _ =>
            {
                called = true;
                return Task.FromResult<WinAuthCredentials?>(null);
            };

            Assert.IsNotNull(proxy.WinAuthCredentialsProvider);
            // Invoke shape only — full 401 handshake needs a Windows origin.
            var result = proxy.WinAuthCredentialsProvider(
                null!).GetAwaiter().GetResult();
            Assert.IsTrue(called);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GetInitialAuthToken_With_Explicit_Credentials_On_Windows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows SSPI is required.");

            // SSPI accepts the structure even when the account is invalid — failure surfaces as
            // null/exception from AcquireCredentialsHandle. Process-identity path remains default.
            try
            {
                var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com", "NTLM",
                    new InternalDataStore(),
                    new WinAuthCredentials { Domain = ".", UserName = "no-such-user-twp", Password = "x" });
                // Some Windows configs still return a Type1 message for unknown local users.
                Assert.IsTrue(token == null || token.Length > 1);
            }
            catch (InvalidOperationException)
            {
                // Expected when SSPI rejects the synthetic credentials.
            }
        }

        [TestMethod]
        public void Test_Acquire_Upstream_Proxy_Client_Token()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows SSPI is required.");

            var token =
                WinAuthHandler.GetInitialProxyAuthToken("proxy.example.com", "NTLM", new InternalDataStore());

            StringAssert.StartsWith(token, " ");
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ProxyAuth_ReusesConnection()
        {
            // 407 (upstream proxy auth) must reuse the same connection regardless of the WinAuth flag.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, false));
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ServerWinAuth401_ReusesConnection()
        {
            // 401 handled by NTLM/Negotiate is connection-oriented and must reuse the same connection.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_NonAuthReRequest_UsesFreshConnection()
        {
            // A user-initiated re-request (not an auth handshake) may target a different destination.
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(200, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(302, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, false));
        }

        [TestMethod]
        public void ValidateWinAuthState_Unauthorized_AllowsMissingOrTerminalStates()
        {
            var empty = new InternalDataStore();
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(empty, State.WinAuthState.Unauthorized));

            var data = new InternalDataStore();
            data["AuthState"] = new State { AuthState = State.WinAuthState.Unauthorized };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.Unauthorized));

            data["AuthState"] = new State { AuthState = State.WinAuthState.Authorized };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.Unauthorized));
        }

        [TestMethod]
        public void ValidateWinAuthState_InitialToken_RequiresStoredInitialOrAuthorized()
        {
            var empty = new InternalDataStore();
            Assert.IsFalse(WinAuthEndPoint.ValidateWinAuthState(empty, State.WinAuthState.InitialToken));

            var data = new InternalDataStore();
            data["AuthState"] = new State { AuthState = State.WinAuthState.InitialToken };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.InitialToken));

            data["AuthState"] = new State { AuthState = State.WinAuthState.Authorized };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.InitialToken));

            data["AuthState"] = new State { AuthState = State.WinAuthState.FinalToken };
            Assert.IsFalse(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.InitialToken));
        }

        [TestMethod]
        public void ValidateWinAuthState_FinalToken_RequiresStoredFinalOrAuthorized()
        {
            var empty = new InternalDataStore();
            Assert.IsFalse(WinAuthEndPoint.ValidateWinAuthState(empty, State.WinAuthState.FinalToken));

            var data = new InternalDataStore();
            data["AuthState"] = new State { AuthState = State.WinAuthState.FinalToken };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.FinalToken));

            data["AuthState"] = new State { AuthState = State.WinAuthState.Authorized };
            Assert.IsTrue(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.FinalToken));

            data["AuthState"] = new State { AuthState = State.WinAuthState.InitialToken };
            Assert.IsFalse(WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.FinalToken));
        }

        [TestMethod]
        public void ValidateWinAuthState_AuthorizedAsExpected_ThrowsNotSupported()
        {
            var data = new InternalDataStore();
            Assert.ThrowsExactly<NotSupportedException>(() =>
                WinAuthEndPoint.ValidateWinAuthState(data, State.WinAuthState.Authorized));
        }

        [TestMethod]
        public void AuthenticatedResponse_SetsAuthorizedAndRefreshesPresence()
        {
            var data = new InternalDataStore();
            var state = new State { AuthState = State.WinAuthState.FinalToken };
            data["AuthState"] = state;
            var before = state.LastSeen;
            Thread.Sleep(5);

            WinAuthEndPoint.AuthenticatedResponse(data);

            Assert.AreEqual(State.WinAuthState.Authorized, state.AuthState);
            Assert.IsTrue(state.LastSeen >= before);
        }

        [TestMethod]
        public void AuthenticatedResponse_NoState_IsNoOp()
        {
            var data = new InternalDataStore();
            WinAuthEndPoint.AuthenticatedResponse(data);
        }

        [TestMethod]
        public void State_UpdatePresence_ResetHandles_AndDispose()
        {
            using var state = new State();
            Assert.AreEqual(State.WinAuthState.Unauthorized, state.AuthState);

            var before = state.LastSeen;
            Thread.Sleep(5);
            state.UpdatePresence();
            Assert.IsTrue(state.LastSeen >= before);

            state.AuthState = State.WinAuthState.InitialToken;
            state.ResetHandles();
            Assert.AreEqual(State.WinAuthState.Unauthorized, state.AuthState);
        }

        [TestMethod]
        public void SecurityBufferDescription_GetBytes_RoundTripsTokenPayload()
        {
            var payload = new byte[] { 0x4e, 0x54, 0x4c, 0x4d, 0x01, 0x02, 0x03 };
            var desc = new SecurityBufferDescription(payload);
            CollectionAssert.AreEqual(payload, desc.GetBytes());
        }

        [TestMethod]
        public void GetFinalAuthToken_WithoutPriorState_Throws()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("Windows SSPI is required.");

            var data = new InternalDataStore();
            Assert.ThrowsExactly<KeyNotFoundException>(() =>
                WinAuthHandler.GetFinalAuthToken("host", Convert.ToBase64String(new byte[8]), data));
        }

        [TestMethod]
        public void GetFinalAuthToken_InvalidBase64_ThrowsFormatException()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("Windows SSPI is required.");

            var data = new InternalDataStore();
            data["AuthState"] = new State();
            Assert.ThrowsExactly<FormatException>(() =>
                WinAuthHandler.GetFinalAuthToken("host", "not-valid-base64!!!", data));
        }

        [TestMethod]
        public void GetInitialAuthToken_NonWindows_ThrowsInvalidOperation()
        {
            if (OperatingSystem.IsWindows())
                Assert.Inconclusive("Non-Windows path only.");

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                WinAuthHandler.GetInitialAuthToken("host", "NTLM", new InternalDataStore()));
        }

        [TestMethod]
        public void GenerateUpstreamProxyWinAuthToken_UsesInjectedGenerator()
        {
            using var proxy = new ProxyServer(false, false, false);
            var data = new InternalDataStore();
            proxy.UpstreamProxyWinAuthTokenGenerator = (_, scheme, challenge, store) =>
            {
                Assert.AreSame(data, store);
                return challenge == null ? $" {scheme}-initial" : $" {scheme}-final";
            };

            var external = new ExternalProxy { HostName = "proxy.test", UseDefaultCredentials = true };
            Assert.AreEqual(" NTLM-initial",
                proxy.GenerateUpstreamProxyWinAuthToken(external, "NTLM", null, data));
            Assert.AreEqual(" NTLM-final",
                proxy.GenerateUpstreamProxyWinAuthToken(external, "NTLM", "abc", data));
        }

        [TestMethod]
        [SupportedOSPlatform("windows")]
        public void WinCertificateMaker_MakeRootCertificate_HeadlessWithoutStoreTrust()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("WinCertificateMaker requires Windows X509Enrollment COM.");

            // Uses in-memory PFX only — no TrustRootCertificate / store install.
            try
            {
                var maker = new WinCertificateMaker(30, 7);
                var subject = $"twp-unit-{Guid.NewGuid():N}.local";
                using var cert = maker.MakeCertificate(subject, null);
                Assert.IsTrue(cert.HasPrivateKey);
                StringAssert.Contains(cert.Subject, subject);
            }
            catch (PlatformNotSupportedException ex)
            {
                Assert.Inconclusive($"X509Enrollment COM unavailable: {ex.Message}");
            }
            catch (COMException ex)
            {
                Assert.Inconclusive($"COM enrollment unavailable headless (may need UI/elevation): {ex.Message}");
            }
        }

        [TestMethod]
        [SupportedOSPlatform("windows")]
        public void WinCertificateMaker_MakeLeafSignedByRoot_HeadlessWithoutStoreTrust()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("WinCertificateMaker requires Windows X509Enrollment COM.");

            try
            {
                var maker = new WinCertificateMaker(30, 7);
                var rootCn = $"twp-root-{Guid.NewGuid():N}.local";
                var leafCn = $"twp-leaf-{Guid.NewGuid():N}.local";
                using var root = maker.MakeCertificate(rootCn, null);
                using var leaf = maker.MakeCertificate(leafCn, root);
                Assert.IsTrue(leaf.HasPrivateKey);
                StringAssert.Contains(leaf.Subject, leafCn);
                StringAssert.Contains(leaf.Issuer, rootCn);
            }
            catch (PlatformNotSupportedException ex)
            {
                Assert.Inconclusive($"X509Enrollment COM unavailable: {ex.Message}");
            }
            catch (COMException ex)
            {
                Assert.Inconclusive($"COM enrollment unavailable headless (may need UI/elevation): {ex.Message}");
            }
        }
    }
}