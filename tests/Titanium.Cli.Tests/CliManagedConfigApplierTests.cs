using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Cli.Tests;

[TestClass]
public class CliManagedConfigApplierTests
{
    private const string ValidYaml = """
        listeners:
          - port: 8000
            type: explicit
        """;

    private const string InvalidYaml = """
        listeners:
          - port: not-a-number
        """;

    [TestMethod]
    public async Task Apply_RejectsInvalidYaml_WithoutReplacingLiveFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-mgr-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ValidYaml, new UTF8Encoding(false));
        var original = await File.ReadAllTextAsync(path);
        var reloads = 0;

        var applier = new CliManagedConfigApplier(path, _ =>
        {
            reloads++;
            return Task.FromResult(new PlusReloadResult { Ok = true, ConfigHash = "h" });
        });

        var result = await applier.ApplyAsync(InvalidYaml, "bad-hash");
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
        Assert.AreEqual(0, reloads);
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task Apply_RestoresBak_WhenReloadFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-mgr-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ValidYaml, new UTF8Encoding(false));

        const string nextYaml = """
            listeners:
              - port: 8001
                type: explicit
            """;

        var applier = new CliManagedConfigApplier(path, _ =>
            Task.FromResult(new PlusReloadResult { Ok = false, Error = "boom" }));

        var result = await applier.ApplyAsync(nextYaml, "next-hash");
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "boom");
        Assert.AreEqual(ValidYaml.Replace("\r\n", "\n").TrimEnd(),
            (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").TrimEnd());
        Assert.IsTrue(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public async Task Apply_ReplacesAndReportsHash_WhenReloadSucceeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-mgr-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ValidYaml, new UTF8Encoding(false));

        const string nextYaml = """
            listeners:
              - port: 8001
                type: explicit
            """;

        var applier = new CliManagedConfigApplier(path, _ =>
            Task.FromResult(new PlusReloadResult { Ok = true, ConfigHash = "applied" }));

        var result = await applier.ApplyAsync(nextYaml, "fallback");
        Assert.IsTrue(result.Ok);
        Assert.AreEqual("applied", result.AppliedHash);
        Assert.AreEqual(nextYaml.Replace("\r\n", "\n").TrimEnd(),
            (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").TrimEnd());
    }
}
