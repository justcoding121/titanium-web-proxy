using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.GeoIp;
using Titanium.Cli.Tests.Geo;

namespace Titanium.Cli.GeoIp.Tests;

/// <summary>
///     Malformed MMDB files must not hang or throw from <see cref="MmdbGeoIpResolver"/>.
///     Bug class: truncated or mutated databases loop or crash the process.
/// </summary>
[TestClass]
public class MmdbGeoIpMutationTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void MutatedDatabase_NeverHangsOrThrowsFromTryGetCountry()
    {
        using var good = new MemoryStream();
        MmdbTestWriter.Write(good, [(IPAddress.Parse("127.0.0.1"), 32, "XX")]);
        var seed = good.ToArray();
        var rng = new Random(42);
        var violations = new List<string>();

        for (var i = 0; i < 200; i++)
        {
            var mutant = (byte[])seed.Clone();
            for (var b = 0; b < mutant.Length; b += 7)
            {
                mutant[b] ^= (byte)(1 + (i % 255));
            }

            if (mutant.Length > 4)
            {
                mutant[rng.Next(mutant.Length)] = (byte)rng.Next(256);
            }

            var path = Path.Combine(Path.GetTempPath(), "twp-mmdb-" + Guid.NewGuid().ToString("N") + ".mmdb");
            try
            {
                File.WriteAllBytes(path, mutant);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var task = Task.Run(() =>
                {
                    try
                    {
                        using var resolver = new MmdbGeoIpResolver(path);
                        _ = resolver.TryGetCountry(IPAddress.Loopback, out _);
                        _ = resolver.TryGetCountry(IPAddress.Parse("8.8.8.8"), out _);
                        resolver.Dispose();
                        resolver.Dispose();
                    }
                    catch (Exception ex) when (
                        ex is InvalidOperationException or IOException or ArgumentException
                            or FormatException or UnauthorizedAccessException
                            or MaxMind.Db.InvalidDatabaseException)
                    {
                        // Constructor may reject a broken file; that is fine.
                    }
                }, cts.Token);

                if (!task.Wait(TimeSpan.FromSeconds(3)))
                {
                    violations.Add($"hang on mutant {i}");
                }
                else if (task.IsFaulted && task.Exception is not null)
                {
                    foreach (var ex in task.Exception.Flatten().InnerExceptions)
                    {
                        if (ex is not (
                            InvalidOperationException or IOException or ArgumentException
                            or FormatException or UnauthorizedAccessException
                            or MaxMind.Db.InvalidDatabaseException or ObjectDisposedException
                            or OperationCanceledException))
                        {
                            violations.Add($"{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join("; ", violations));
    }
}
