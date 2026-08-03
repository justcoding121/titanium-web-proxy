using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class GenericResponseAndLittleEndianTests
{
    [TestMethod]
    [DataRow(100, "Continue")]
    [DataRow(101, "Switching Protocols")]
    [DataRow(200, "OK")]
    [DataRow(201, "Created")]
    [DataRow(204, "No Content")]
    [DataRow(301, "Moved Permanently")]
    [DataRow(302, "Found")]
    [DataRow(304, "Not Modified")]
    [DataRow(400, "Bad Request")]
    [DataRow(401, "Unauthorized")]
    [DataRow(403, "Forbidden")]
    [DataRow(404, "Not Found")]
    [DataRow(429, "Too Many Requests")]
    [DataRow(451, "Unavailable For Legal Reasons")]
    [DataRow(500, "Internal Server Error")]
    [DataRow(502, "Bad Gateway")]
    [DataRow(503, "Service Unavailable")]
    [DataRow(511, "Network Authentication Required")]
    public void GenericResponse_Get_MapsKnownStatusCodes(int code, string expected)
    {
        Assert.AreEqual(expected, GenericResponse.Get(code));
    }

    [TestMethod]
    public void GenericResponse_Get_UnknownCodes_ReturnNull()
    {
        Assert.IsNull(GenericResponse.Get(418));
        Assert.IsNull(GenericResponse.Get(999));
    }

    [TestMethod]
    public void GenericResponse_Get_CoversFullHttpStatusTable()
    {
        // Touch every mapped branch so status-table coverage does not depend on sparse DataRows.
        int[] codes =
        [
            100, 101, 102, 103,
            200, 201, 202, 203, 204, 205, 206, 207, 208, 226,
            300, 301, 302, 303, 304, 305, 307, 308,
            400, 401, 402, 403, 404, 405, 406, 407, 408, 409, 410, 411, 412, 413, 414, 415, 416, 417,
            421, 422, 423, 424, 426, 428, 429, 431, 451,
            500, 501, 502, 503, 504, 505, 506, 507, 508, 510, 511
        ];
        foreach (var code in codes)
            Assert.IsFalse(string.IsNullOrEmpty(GenericResponse.Get(code)), $"missing mapping for {code}");
    }

    [TestMethod]
    public void GenericResponse_HttpStatusCodeCtor_FillsDescription()
    {
        var response = new GenericResponse(HttpStatusCode.NotFound);
        Assert.AreEqual(404, response.StatusCode);
        Assert.AreEqual("Not Found", response.StatusDescription);
    }

    [TestMethod]
    public void GenericResponse_ExplicitDescriptionCtor()
    {
        var response = new GenericResponse(599, "Custom");
        Assert.AreEqual(599, response.StatusCode);
        Assert.AreEqual("Custom", response.StatusDescription);
    }

    [TestMethod]
    public void LittleEndian_RoundTripsPrimitiveTypes()
    {
        CollectionAssert.AreEqual(new byte[] { 1 }, LittleEndian.GetBytes(true));
        CollectionAssert.AreEqual(new byte[] { 0 }, LittleEndian.GetBytes(false));

        Assert.AreEqual((short)0x1234, LittleEndian.ToInt16(LittleEndian.GetBytes((short)0x1234), 0));
        Assert.AreEqual(0x12345678, LittleEndian.ToInt32(LittleEndian.GetBytes(0x12345678), 0));
        Assert.AreEqual(0x1122334455667788L, LittleEndian.ToInt64(LittleEndian.GetBytes(0x1122334455667788L), 0));
        Assert.AreEqual((ushort)0xABCD, LittleEndian.ToUInt16(LittleEndian.GetBytes((ushort)0xABCD), 0));
        Assert.AreEqual(0xAABBCCDDu, LittleEndian.ToUInt32(LittleEndian.GetBytes(0xAABBCCDDu), 0));
        Assert.AreEqual(0x0102030405060708UL, LittleEndian.ToUInt64(LittleEndian.GetBytes(0x0102030405060708UL), 0));
        Assert.AreEqual('Z', LittleEndian.ToChar(LittleEndian.GetBytes('Z'), 0));
        Assert.AreEqual(3.5f, LittleEndian.ToSingle(LittleEndian.GetBytes(3.5f), 0));
        Assert.AreEqual(Math.PI, LittleEndian.ToDouble(LittleEndian.GetBytes(Math.PI), 0));
        Assert.IsTrue(LittleEndian.ToBoolean(LittleEndian.GetBytes(true), 0));
    }
}
