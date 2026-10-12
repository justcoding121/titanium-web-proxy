using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SazImportTests
{
    [TestMethod]
    public void ValidArchive_ImportsRequestResponse_AndLeavesGzipEncoded()
    {
        var gzipBody = new byte[] { 1, 2, 3, 4 };
        using var zip = Zip(
            ("raw/3_c.txt", "POST /v1 HTTP/1.1\r\nHost: ex.test\r\nContent-Type: text/plain\r\n\r\nhi"),
            ("raw/3_s.txt", "HTTP/1.1 201 Created\r\nContent-Encoding: gzip\r\n\r\n" + Encoding.Latin1.GetString(gzipBody)),
            ("raw/3_m.xml", "<Session><SessionID>3</SessionID></Session>"),
            ("raw/../outside.txt", "nope"),
            ("not-raw.txt", "nope"));
        var result = SazImport.Read(zip, startId: 40);
        Assert.AreEqual(SazImportStatus.Ok, result.Status);
        Assert.AreEqual(1, result.Sessions.Count);
        var session = result.Sessions[0];
        Assert.AreEqual(40, session.Id);
        Assert.AreEqual(0, session.ConnectionId);
        Assert.AreEqual("POST", session.Method);
        Assert.AreEqual("https://ex.test/v1", session.Url);
        Assert.AreEqual(201, session.StatusCode);
        Assert.IsTrue(session.TimingEstimated);
        StringAssert.Contains(session.ResponseHeadersText, "Content-Encoding: gzip");
        CollectionAssert.AreEqual(gzipBody, session.ResponseBodyBytes);
    }

    [TestMethod]
    public void Connect_IsATunnel_AndChunkedBodyIsDecoded()
    {
        using var zip = Zip(
            ("raw/1_c.txt", "CONNECT ex.test:443 HTTP/1.1\r\nHost: ex.test:443\r\n\r\n"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"));
        var result = SazImport.Read(zip, 1);
        Assert.AreEqual(SazImportStatus.Ok, result.Status);
        Assert.IsTrue(result.Sessions[0].IsTunnel);
        Assert.AreEqual("hello", result.Sessions[0].ResponseBodyText);
    }

    [TestMethod]
    public void HostileMetadata_IsRejected()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><Session>&xxe;</Session>";
        using var zip = Zip(("raw/1_c.txt", "GET / HTTP/1.1\r\nHost: ex.test\r\n\r\n"), ("raw/1_m.xml", xml));
        var result = SazImport.Read(zip, 1);
        Assert.AreEqual(SazImportStatus.UnsafeMetadata, result.Status);
        Assert.AreEqual(0, result.Sessions.Count);
    }

    [TestMethod]
    public void OversizedEntry_AndTooManySessions_AreRejected()
    {
        var limits = new SazLimits(MaxSessions: 1, MaxEntryBytes: 64, MaxTotalBytes: 256);
        using var big = Zip(("raw/1_c.txt", new string('A', 200)));
        Assert.AreEqual(SazImportStatus.TooLarge, SazImport.Read(big, 1, limits).Status);

        using var many = Zip(
            ("raw/1_c.txt", "GET / HTTP/1.1\r\nHost: a\r\n\r\n"),
            ("raw/2_c.txt", "GET / HTTP/1.1\r\nHost: b\r\n\r\n"));
        Assert.AreEqual(SazImportStatus.TooLarge, SazImport.Read(many, 1, limits).Status);
    }

    [TestMethod]
    public void Truncated_AndEncrypted_AreClearFailures()
    {
        var truncated = new MemoryStream(Encoding.ASCII.GetBytes("PK"));
        Assert.AreEqual(SazImportStatus.NotAnArchive, SazImport.Read(truncated, 1).Status);

        var encrypted = EncryptedLocalHeader();
        Assert.AreEqual(SazImportStatus.Encrypted, SazImport.Read(encrypted, 1).Status);
    }

    [TestMethod]
    public void WrongNames_ImportNothing_AndDoNotWriteFiles()
    {
        var dir = Directory.GetCurrentDirectory();
        var before = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly).Length;
        using var zip = Zip(("raw/nope.txt", "GET / HTTP/1.1\r\n\r\n"), ("../escape.txt", "x"));
        var result = SazImport.Read(zip, 1);
        Assert.AreEqual(SazImportStatus.Empty, result.Status);
        Assert.AreEqual(before, Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly).Length);
    }

    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream EncryptedLocalHeader()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("raw/1_c.txt");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("GET / HTTP/1.1\r\nHost: ex.test\r\n\r\n");
        }

        var bytes = ms.ToArray();
        bytes[6] = 1;
        for (var i = 0; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                bytes[i + 8] = 1;
                break;
            }
        }

        return new MemoryStream(bytes);
    }
}
