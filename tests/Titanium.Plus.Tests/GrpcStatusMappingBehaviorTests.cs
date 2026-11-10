using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Grpc;

namespace Titanium.Plus.Tests;

/// <summary>
///     Full google.rpc.Code → HTTP table and status JSON escaping.
///     Bug classes: unmapped codes leak as 200; quotes/newlines break JSON clients.
/// </summary>
[TestClass]
public class GrpcStatusMappingBehaviorTests
{
    [TestMethod]
    public void ToHttpStatus_MapsEveryKnownCode_AndDefaultsUnknown()
    {
        int[] expected =
        [
            200, 499, 500, 400, 504, 404, 409, 403, 429, 400, 409, 400, 501, 500, 503, 500, 401,
        ];
        for (var code = 0; code < expected.Length; code++)
        {
            Assert.AreEqual(expected[code], GrpcStatusMapping.ToHttpStatus(code), $"grpc code {code}");
        }

        Assert.AreEqual(500, GrpcStatusMapping.ToHttpStatus(-1));
        Assert.AreEqual(500, GrpcStatusMapping.ToHttpStatus(99));
    }

    [TestMethod]
    public void FormatStatusJson_OmitsEmptyMessage_AndEscapesControlChars()
    {
        Assert.AreEqual("{\"code\":5}", GrpcStatusMapping.FormatStatusJson(5, null));
        Assert.AreEqual("{\"code\":5}", GrpcStatusMapping.FormatStatusJson(5, ""));
        var json = GrpcStatusMapping.FormatStatusJson(3, "a\"b\\c\n\r\td");
        Assert.AreEqual("{\"code\":3,\"message\":\"a\\\"b\\\\c\\n\\r\\td\"}", json);
    }
}
