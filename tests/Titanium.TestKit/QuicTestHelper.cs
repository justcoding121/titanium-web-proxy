#pragma warning disable CA1416

using System;
using System.Net.Quic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.TestKit;

/// <summary>
/// Optional QUIC for integration tests. <c>TITANIUM_REQUIRE_QUIC=1</c> fails the test when
/// <see cref="QuicListener.IsSupported"/> is false. Otherwise the test is inconclusive.
/// </summary>
public static class QuicTestHelper
{
    public static bool RequireQuicEnv =>
        string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REQUIRE_QUIC"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REQUIRE_QUIC"), "true", StringComparison.OrdinalIgnoreCase);

    public static void RequireQuic()
    {
        if (QuicListener.IsSupported && QuicConnection.IsSupported)
        {
            return;
        }

        if (RequireQuicEnv)
        {
            Assert.Fail("QUIC is required but System.Net.Quic is not supported. Install libmsquic, or unset TITANIUM_REQUIRE_QUIC.");
        }

        Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }
}
