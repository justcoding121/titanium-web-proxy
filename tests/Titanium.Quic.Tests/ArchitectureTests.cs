using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void HostRuns()
    {
        Assert.AreEqual("Titanium.Quic", AssemblyMarker.Name);
    }

    [TestMethod]
    public void LibraryDoesNotReferenceTheProxyAndDoesNotPInvoke()
    {
        Assembly assembly = typeof(AssemblyMarker).Assembly;
        foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
        {
            string name = referenced.Name ?? string.Empty;
            Assert.IsFalse(name.StartsWith("Titanium.Web.Proxy", StringComparison.Ordinal), name);
        }

        byte[]? token = assembly.GetName().GetPublicKeyToken();
        Assert.IsNotNull(token);
        Assert.IsTrue(token!.Length > 0);

        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl), method.Name);
            }
        }
    }
}
