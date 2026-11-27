using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

/// <summary>
/// Local check that an installed Chrome still produces the pinned ClientHello shape.
/// It does not run unless TITANIUM_CHROME_DRIFT=1, so a normal suite does not launch a browser.
/// </summary>
[TestClass]
public sealed class ChromeFingerprintDriftTests
{
    [TestMethod]
    [TestCategory("Explicit")]
    public void InstalledChromeMatchesThePinnedInvariants()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TITANIUM_CHROME_DRIFT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        string chrome = FindChrome();
        byte[] pinned = File.ReadAllBytes(FindPinned());
        for (int i = 0; i < 8; i++)
        {
            byte[] hello = Capture(chrome);
            ChromeHelloInvariants.AssertMatch(pinned, hello);
        }
    }

    private static byte[] Capture(string chrome)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string profile = Path.Combine(Path.GetTempPath(), "twp-chrome-drift-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(profile);
        using var process = new Process();
        process.StartInfo.FileName = chrome;
        process.StartInfo.ArgumentList.Add("--user-data-dir=" + profile);
        process.StartInfo.ArgumentList.Add("--no-first-run");
        process.StartInfo.ArgumentList.Add("--no-default-browser-check");
        process.StartInfo.ArgumentList.Add("--disable-sync");
        process.StartInfo.ArgumentList.Add("--disable-background-networking");
        process.StartInfo.ArgumentList.Add("--ignore-certificate-errors");
        process.StartInfo.ArgumentList.Add("https://localhost:" + port + "/");
        process.StartInfo.UseShellExecute = false;
        process.Start();
        try
        {
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(20)))
            {
                Assert.Fail("Chrome did not connect.");
            }

            using TcpClient client = accept.Result;
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            var header = new byte[5];
            ReadExact(stream, header);
            int length = (header[3] << 8) | header[4];
            Assert.AreEqual(22, header[0]);
            Assert.IsTrue(length > 0 && length <= 16384);
            var body = new byte[length];
            ReadExact(stream, body);
            var record = new byte[5 + length];
            header.CopyTo(record, 0);
            body.CopyTo(record, 5);
            return record;
        }
        finally
        {
            listener.Stop();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0)
            {
                Assert.Fail("Chrome closed before the ClientHello was complete.");
            }

            got += n;
        }
    }

    private static string FindChrome()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("Chrome is not installed.");
        return "";
    }

    private static string FindPinned()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string path = Path.Combine(dir.FullName, "tests", "Titanium.Tls.Tests", "Fixtures", "captures", "chrome", "158.0.8127.89", "tcp", "hello-001.bin");
            if (File.Exists(path))
            {
                return path;
            }

            dir = dir.Parent;
        }

        Assert.Fail("The pinned ClientHello is missing.");
        return "";
    }
}
