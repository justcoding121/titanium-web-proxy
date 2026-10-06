using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class UnknownKeyStrictTests
{
    [TestMethod]
    public async Task TestStrict_Exits1_OnUnknownKey_AndPassesWithoutStrict()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-unknown-" + Guid.NewGuid().ToString("n") + ".yaml");
        await File.WriteAllTextAsync(path, """
            server:
              limtis:
                maxHeaderCount: 10
            """);

        try
        {
            var strict = await TestCommand.ExecuteAsync(["test", "-c", path, "--strict"]);
            var loose = await TestCommand.ExecuteAsync(["test", "-c", path]);
            Assert.AreEqual(1, strict);
            Assert.AreEqual(0, loose);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
