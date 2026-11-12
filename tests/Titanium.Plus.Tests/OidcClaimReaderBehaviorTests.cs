using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;

namespace Titanium.Plus.Tests;

/// <summary>
///     Pure <see cref="OidcClaimReader"/> coverage for nested claims and header-safe flattening.
///     Bug classes: nested groups.claim miss, array numbers dropped, control chars leak into headers.
/// </summary>
[TestClass]
public class OidcClaimReaderBehaviorTests
{
    private static readonly string[] ExpectedOpsViewerRoles = ["ops", "viewer"];
    private static readonly string[] ExpectedSingleRole = ["single"];

    [TestMethod]
    public void Find_ReadsDirectAndDottedPaths()
    {
        using var doc = JsonDocument.Parse("""{"groups":["a"],"nested":{"groups":{"claim":["ops"]}},"email":"x@t"}""");
        var root = doc.RootElement;

        Assert.AreEqual(JsonValueKind.Array, OidcClaimReader.Find(root, "groups")!.Value.ValueKind);
        Assert.AreEqual(JsonValueKind.Array, OidcClaimReader.Find(root, "nested.groups.claim")!.Value.ValueKind);
        Assert.IsNull(OidcClaimReader.Find(root, "missing"));
        Assert.IsNull(OidcClaimReader.Find(root, "nested.missing.claim"));
        Assert.IsNull(OidcClaimReader.Find(JsonDocument.Parse("[]").RootElement, "groups"));
    }

    [TestMethod]
    public void AddAll_CollectsOnlyNonEmptyStrings()
    {
        using var doc = JsonDocument.Parse("""["ops","",42,true,"viewer"]""");
        var into = new List<string>();
        OidcClaimReader.AddAll(doc.RootElement, into);
        CollectionAssert.AreEqual(ExpectedOpsViewerRoles, into);

        into.Clear();
        OidcClaimReader.AddAll(JsonDocument.Parse("\"single\"").RootElement, into);
        CollectionAssert.AreEqual(ExpectedSingleRole, into);

        into.Clear();
        OidcClaimReader.AddAll(JsonDocument.Parse("\"\"").RootElement, into);
        Assert.AreEqual(0, into.Count);

        into.Clear();
        OidcClaimReader.AddAll(JsonDocument.Parse("99").RootElement, into);
        Assert.AreEqual(0, into.Count);
    }

    [TestMethod]
    public void Flatten_JoinsArrays_AndSanitizesControlsAndLength()
    {
        Assert.AreEqual("ops,viewer", OidcClaimReader.Flatten(JsonDocument.Parse("""["ops","viewer"]""").RootElement));
        Assert.AreEqual("42", OidcClaimReader.Flatten(JsonDocument.Parse("42").RootElement));
        Assert.AreEqual("true", OidcClaimReader.Flatten(JsonDocument.Parse("true").RootElement));
        Assert.AreEqual("false", OidcClaimReader.Flatten(JsonDocument.Parse("false").RootElement));
        Assert.IsNull(OidcClaimReader.Flatten(JsonDocument.Parse("{}").RootElement));
        Assert.IsNull(OidcClaimReader.Flatten(JsonDocument.Parse("null").RootElement));

        var dirty = "ab\r\n\tc" + new string('x', 1100);
        using var dirtyDoc = JsonDocument.Parse(JsonSerializer.Serialize(dirty));
        var flat = OidcClaimReader.Flatten(dirtyDoc.RootElement)!;
        Assert.IsFalse(flat.Contains('\r', StringComparison.Ordinal));
        Assert.IsFalse(flat.Contains('\n', StringComparison.Ordinal));
        Assert.IsFalse(flat.Contains('\t', StringComparison.Ordinal));
        Assert.AreEqual(1024, flat.Length);
        Assert.IsTrue(flat.StartsWith("abc", StringComparison.Ordinal));
    }
}
