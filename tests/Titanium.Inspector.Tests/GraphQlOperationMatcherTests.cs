using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class GraphQlOperationMatcherTests
{
    [TestMethod]
    public void TryGetOperationName_FromJsonField()
    {
        Assert.IsTrue(GraphQlOperationMatcher.TryGetOperationName(
            """{"operationName":"GetUser","query":"query GetUser { user { id } }"}""",
            out var name));
        Assert.AreEqual("GetUser", name);
    }

    [TestMethod]
    public void MatchesOperation_FiltersAutoResponder()
    {
        var vm = new AutoResponderViewModel { Enabled = true };
        vm.Rules.Add(new AutoResponderRule
        {
            MatchUrl = "*graphql*",
            StatusCode = 200,
            Body = "user-op",
            GraphQlOperationName = "GetUser",
            Enabled = true,
        });
        Assert.IsFalse(vm.TryMatch("http://x/graphql", """{"operationName":"Other"}""", out _));
        Assert.IsTrue(vm.TryMatch("http://x/graphql", """{"operationName":"GetUser"}""", out var rule));
        Assert.AreEqual("user-op", rule!.Body);
        StringAssert.Contains(rule.Display, "GraphQL:GetUser");
    }
}
