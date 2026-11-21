using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Inspector.Tests;

[TestClass]
public class XOpenDisplayStartupTests
{
    [TestMethod]
    public void IsXOpenDisplayFailure_MatchesAvaloniaMessage()
    {
        var ex = new Exception("XOpenDisplay failed");
        Assert.IsTrue(Program.IsXOpenDisplayFailure(ex));
    }

    [TestMethod]
    public void IsXOpenDisplayFailure_MatchesInnerException()
    {
        var ex = new InvalidOperationException("startup", new Exception("XOpenDisplay failed"));
        Assert.IsTrue(Program.IsXOpenDisplayFailure(ex));
    }

    [TestMethod]
    public void IsXOpenDisplayFailure_RejectsOtherFailures()
    {
        Assert.IsFalse(Program.IsXOpenDisplayFailure(new Exception("No usable version of libssl was found")));
        Assert.IsFalse(Program.IsXOpenDisplayFailure(new InvalidOperationException("display")));
    }
}
