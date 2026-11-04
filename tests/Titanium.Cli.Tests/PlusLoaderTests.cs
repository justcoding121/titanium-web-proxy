using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class PlusLoaderTests
{
    [TestMethod]
    public void TryLoadFrom_UsesDefaultContext_ReusesAssembly_AndResolvesOnlyListedNames()
    {
        var plusDir = LocatePlusOutputDirectory();
        var stage = Path.Combine(Path.GetTempPath(), "twp-plus-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var dllNames = new List<string>();
            foreach (var file in Directory.EnumerateFiles(plusDir, "*.dll"))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(stage, name), overwrite: true);
                dllNames.Add(name);
            }

            dllNames.Add("Titanium.Plus.files");
            dllNames.Sort(StringComparer.Ordinal);
            File.WriteAllLines(Path.Combine(stage, "Titanium.Plus.files"), dllNames);

            Assert.IsNull(
                PlusLoader.TryResolveListedDependency(new AssemblyName("NotAPlusBundleAssembly")),
                "A name absent from Titanium.Plus.files must not resolve.");

            var module = PlusLoader.TryLoadFrom(stage, out var warning);
            Assert.IsNull(warning, warning);
            Assert.IsNotNull(module);
            var plusAssembly = module.GetType().Assembly;
            Assert.AreSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(plusAssembly));

            var again = PlusLoader.TryLoadFrom(stage, out var secondWarning);
            Assert.IsNull(secondWarning, secondWarning);
            Assert.AreSame(plusAssembly, again!.GetType().Assembly);

            var bcrypt = PlusLoader.TryResolveListedDependency(new AssemblyName("BCrypt.Net-Next"));
            Assert.IsNotNull(bcrypt, "BCrypt.Net-Next is in the Plus bundle and must resolve.");
            Assert.AreSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(bcrypt));

            Assert.IsNull(PlusLoader.TryResolveListedDependency(new AssemblyName("NotAPlusBundleAssembly")));
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (Exception)
            {
                // The loaded assemblies keep the staged files mapped. The temp directory can stay.
            }
        }
    }

    private static string LocatePlusOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var plus = Path.Combine(dir.FullName, "src", "Titanium.Plus", "bin", config, "net10.0", "Titanium.Plus.dll");
                if (File.Exists(plus))
                {
                    return Path.GetDirectoryName(plus)!;
                }
            }

            dir = dir.Parent;
        }

        Assert.Fail("Build src/Titanium.Plus before PlusLoaderTests.");
        return "";
    }
}
