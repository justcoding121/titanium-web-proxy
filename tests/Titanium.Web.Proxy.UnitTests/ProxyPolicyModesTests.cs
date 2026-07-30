using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyPolicyModesTests
{
    [TestMethod]
    public void AllEnforce_HasEveryFamilyEnforcedAndAmbiguousFramingDisabled()
    {
        var modes = ProxyPolicyModes.AllEnforce;

        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.DecompressionRatio]);
        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.HeaderLimits]);
        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.AdmissionControl]);
        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.Http2AbuseBudget]);
        Assert.IsFalse(modes.AllowAmbiguousFraming);
    }

    [TestMethod]
    public void Create_AssignsEachFamilyIndependently()
    {
        var modes = ProxyPolicyModes.Create(
            bodyBudget: PolicyMode.Enforce,
            decompressionRatio: PolicyMode.Observe,
            headerLimits: PolicyMode.Disabled,
            admissionControl: PolicyMode.Observe,
            http2AbuseBudget: PolicyMode.Enforce);

        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Observe, modes[PolicyFamily.DecompressionRatio]);
        Assert.AreEqual(PolicyMode.Disabled, modes[PolicyFamily.HeaderLimits]);
        Assert.AreEqual(PolicyMode.Observe, modes[PolicyFamily.AdmissionControl]);
        Assert.AreEqual(PolicyMode.Enforce, modes[PolicyFamily.Http2AbuseBudget]);
    }

    [TestMethod]
    public void With_ReturnsNewSnapshot_LeavingOriginalUntouched()
    {
        var original = ProxyPolicyModes.AllEnforce;
        var modified = original.With(PolicyFamily.BodyBudget, PolicyMode.Observe);

        Assert.AreEqual(PolicyMode.Enforce, original[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Observe, modified[PolicyFamily.BodyBudget]);

        // Every other family is copied over unchanged.
        Assert.AreEqual(PolicyMode.Enforce, modified[PolicyFamily.AdmissionControl]);
    }

    [TestMethod]
    public void WithAllObservedExceptDisabled_DropsEnforceToObserve_ButLeavesDisabledFamiliesAlone()
    {
        var original = ProxyPolicyModes.Create(
            bodyBudget: PolicyMode.Enforce,
            decompressionRatio: PolicyMode.Enforce,
            headerLimits: PolicyMode.Disabled,
            admissionControl: PolicyMode.Observe,
            http2AbuseBudget: PolicyMode.Enforce);

        var observed = original.WithAllObservedExceptDisabled();

        Assert.AreEqual(PolicyMode.Observe, observed[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Observe, observed[PolicyFamily.DecompressionRatio]);
        Assert.AreEqual(PolicyMode.Disabled, observed[PolicyFamily.HeaderLimits]);
        Assert.AreEqual(PolicyMode.Observe, observed[PolicyFamily.AdmissionControl]);
        Assert.AreEqual(PolicyMode.Observe, observed[PolicyFamily.Http2AbuseBudget]);
    }

    [TestMethod]
    public void WithAllowAmbiguousFramingEnabled_OnlyTogglesTheEscapeHatch_NotAnyFamily()
    {
        var original = ProxyPolicyModes.AllEnforce;
        var withEscape = original.WithAllowAmbiguousFramingEnabled();

        Assert.IsFalse(original.AllowAmbiguousFraming);
        Assert.IsTrue(withEscape.AllowAmbiguousFraming);
        Assert.AreEqual(PolicyMode.Enforce, withEscape[PolicyFamily.BodyBudget]);
    }

    [TestMethod]
    public void Create_NeverAcceptsAllowAmbiguousFraming_AsAParameter()
    {
        // Structural guard for the type-level guarantee that no profile can accidentally enable the
        // ambiguous-framing escape hatch: Create()'s signature has no such parameter, so the only
        // snapshot returned by it always starts with AllowAmbiguousFraming == false.
        var modes = ProxyPolicyModes.Create(
            PolicyMode.Disabled, PolicyMode.Disabled, PolicyMode.Disabled, PolicyMode.Disabled,
            PolicyMode.Disabled);

        Assert.IsFalse(modes.AllowAmbiguousFraming);
    }
}
