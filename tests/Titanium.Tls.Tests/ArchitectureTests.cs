using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void HostRuns()
    {
        Assert.AreEqual("Titanium.Tls", AssemblyMarker.Name);
    }

    [TestMethod]
    public void LibraryDoesNotReferenceTheProxyAndDoesNotPInvoke()
    {
        AssertLibrary(typeof(AssemblyMarker).Assembly);
    }

    internal static void AssertLibrary(Assembly assembly)
    {
        foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
        {
            string name = referenced.Name ?? string.Empty;
            Assert.IsFalse(
                name.StartsWith("Titanium.Web.Proxy", StringComparison.Ordinal),
                name);
        }

        byte[]? token = assembly.GetName().GetPublicKeyToken();
        Assert.IsNotNull(token);
        Assert.IsTrue(token!.Length > 0, assembly.GetName().Name);

        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(
                    method.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
                    type.FullName + "." + method.Name);
            }
        }
    }
}
