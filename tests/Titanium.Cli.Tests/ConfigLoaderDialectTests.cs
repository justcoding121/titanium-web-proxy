using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Parsers;

namespace Titanium.Cli.Tests;

[TestClass]
public class ConfigLoaderDialectTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init() => _tempDir = Directory.CreateTempSubdirectory("twp-cfg-dialect-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [TestMethod]
    public void Load_GenericJsonWithPlus_UsesNativeDialectAndKeepsPlus()
    {
        var path = Path.Combine(_tempDir, "edge-plus.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": "7.0",
              "listeners": [ { "host": "127.0.0.1", "port": 8000, "decryptSsl": false } ],
              "logging": { "enabled": true, "minimumLevel": "Information", "enableConsole": true },
              "plus": {
                "enabled": true,
                "controlPlane": { "host": "127.0.0.1", "port": 9080, "sharedSecret": "test-secret" },
                "options": {
                  "grpc.transcode.enabled": "true",
                  "grpc.transcode.descriptorSet": "/tmp/greeter.pb",
                  "grpc.transcode.services": "helloworld.Greeter"
                }
              }
            }
            """);

        var loaded = ConfigLoader.Load(path);
        Assert.AreEqual("twp-native", loaded.Dialect);
        Assert.IsNotNull(loaded.Config.Plus);
        Assert.IsTrue(loaded.Config.Plus!.Enabled);
        Assert.AreEqual("true", loaded.Config.Plus.Options!["grpc.transcode.enabled"]);
        Assert.IsNotNull(loaded.Config.Logging);
        Assert.IsTrue(loaded.Config.Logging!.Enabled);
    }

    [TestMethod]
    public void Load_BareReverseProxyJson_UsesReverseProxyDialect()
    {
        var path = Path.Combine(_tempDir, "routes-only.json");
        File.WriteAllText(path, """
            {
              "listeners": [ { "host": "127.0.0.1", "port": 8000 } ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "destinations": [ { "id": "d1", "address": "127.0.0.1", "port": 9 } ]
                }
              ]
            }
            """);

        var loaded = ConfigLoader.Load(path);
        Assert.AreEqual("json-reverse-proxy", loaded.Dialect);
        Assert.IsNull(loaded.Config.Plus);
        Assert.AreEqual(1, loaded.Config.Routes.Count);
    }

    [TestMethod]
    public void LooksLikeNativeTwpJson_DetectsSchemaVersionAndPlus()
    {
        var withSchema = Path.Combine(_tempDir, "a.json");
        File.WriteAllText(withSchema, """{ "schemaVersion": "7.0", "listeners": [] }""");
        Assert.IsTrue(ConfigLoader.LooksLikeNativeTwpJson(withSchema));

        var withPlus = Path.Combine(_tempDir, "b.json");
        File.WriteAllText(withPlus, """{ "plus": { "enabled": true }, "listeners": [] }""");
        Assert.IsTrue(ConfigLoader.LooksLikeNativeTwpJson(withPlus));

        var bare = Path.Combine(_tempDir, "c.json");
        File.WriteAllText(bare, """{ "listeners": [ { "port": 1 } ] }""");
        Assert.IsFalse(ConfigLoader.LooksLikeNativeTwpJson(bare));
    }
}
