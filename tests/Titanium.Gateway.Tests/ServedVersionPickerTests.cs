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
}
