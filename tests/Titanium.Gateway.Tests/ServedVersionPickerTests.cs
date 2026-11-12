using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

[TestClass]
public sealed class ServedVersionPickerTests
{
    [TestMethod]
    public void Pick_PrefersV1WhenPresent()
    {
        Assert.AreEqual("v1", ServedVersionPicker.Pick("tlsroutes", ["v1alpha2", "v1"]));
    }

    [TestMethod]
    public void Pick_FallsBackToAlpha()
    {
        Assert.AreEqual("v1alpha2", ServedVersionPicker.Pick("tcproutes", ["v1alpha2"]));
    }

    [TestMethod]
    public void Pick_ReturnsNullWhenAbsent()
    {
        Assert.IsNull(ServedVersionPicker.Pick("udproutes", ["v1beta1"]));
    }

    [TestMethod]
    public void Pick_BackendTlsPolicies_PrefersNewestKnown()
    {
        // Known order is v1, v1alpha3, v1beta1, v1alpha2 — alpha3 ranks above beta1.
        Assert.AreEqual("v1alpha3", ServedVersionPicker.Pick("backendtlspolicies", ["v1alpha2", "v1beta1", "v1alpha3"]));
        Assert.AreEqual("v1beta1", ServedVersionPicker.Pick("backendtlspolicies", ["v1beta1", "v1alpha2"]));
        Assert.AreEqual("v1", ServedVersionPicker.Pick("backendtlspolicies", ["v1", "v1alpha3"]));
    }

    [TestMethod]
    public void Pick_UnknownPlural_OrGrpcRoutesWithoutV1_ReturnsNull()
    {
        Assert.IsNull(ServedVersionPicker.Pick("httproutes", ["v1"]));
        Assert.IsNull(ServedVersionPicker.Pick("grpcroutes", ["v1alpha2"]));
        Assert.AreEqual("v1", ServedVersionPicker.Pick("grpcroutes", ["v1"]));
    }
}
