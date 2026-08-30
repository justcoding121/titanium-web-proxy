using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionScriptHostTests
{
    [TestMethod]
    public void Interpret_Empty_ReturnsDefaults()
    {
        var empty = SessionScriptHost.Interpret(null);
        Assert.IsFalse(empty.Abort);
        Assert.IsNull(empty.StatusCode);
        Assert.AreEqual(0, empty.Headers.Count);

        var whitespace = SessionScriptHost.Interpret("   \n  ");
        Assert.IsFalse(whitespace.Abort);
        Assert.AreEqual(0, whitespace.Headers.Count);
    }

    [TestMethod]
    public void Interpret_Comments_AreIgnored()
    {
        var result = SessionScriptHost.Interpret("""
            # comment
            // also a comment

            set-header X-A: 1
            """);
        Assert.AreEqual(1, result.Headers.Count);
        Assert.AreEqual("X-A", result.Headers[0].Name);
        Assert.AreEqual("1", result.Headers[0].Value);
    }

    [TestMethod]
    public void Interpret_Abort()
    {
        var result = SessionScriptHost.Interpret("abort");
        Assert.IsTrue(result.Abort);
    }

    [TestMethod]
    public void Interpret_SetStatus()
    {
        var result = SessionScriptHost.Interpret("set-status 404");
        Assert.AreEqual(404, result.StatusCode);
    }

    [TestMethod]
    public void Interpret_SetHeader()
    {
        var result = SessionScriptHost.Interpret("set-header Content-Type: application/json");
        Assert.AreEqual(1, result.Headers.Count);
        Assert.AreEqual("Content-Type", result.Headers[0].Name);
        Assert.AreEqual("application/json", result.Headers[0].Value);
    }

    [TestMethod]
    public void Interpret_Mixed()
    {
        var result = SessionScriptHost.Interpret("""
            # prelude
            set-header X-Test: yes
            set-status 418
            abort
            set-header X-After: no
            """);
        Assert.IsTrue(result.Abort);
        Assert.AreEqual(418, result.StatusCode);
        Assert.AreEqual(2, result.Headers.Count);
        Assert.AreEqual("X-Test", result.Headers[0].Name);
        Assert.AreEqual("X-After", result.Headers[1].Name);
    }

    [TestMethod]
    public void Interpret_InvalidSetStatus_Ignored()
    {
        var result = SessionScriptHost.Interpret("set-status not-a-number");
        Assert.IsNull(result.StatusCode);
    }

    [TestMethod]
    public void Interpret_HeaderWithoutColon_Ignored()
    {
        var result = SessionScriptHost.Interpret("set-header NoColon");
        Assert.AreEqual(0, result.Headers.Count);
    }
}
