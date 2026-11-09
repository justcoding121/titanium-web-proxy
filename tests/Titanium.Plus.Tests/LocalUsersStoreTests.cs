using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Security;

namespace Titanium.Plus.Tests;

/// <summary>
/// Unit and file-system tests for <see cref="LocalUsersStore"/>: line parsing, round-trip
/// authentication, corrupted files, and hot-reload detection.
/// </summary>
[TestClass]
public class LocalUsersStoreTests
{
    // ---------------------------------------------------------------- ParseLine

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("# comment")]
    [DataRow("  # indented comment")]
    public void ParseLine_BlankOrComment_ReturnsNull(string line) =>
        Assert.IsNull(LocalUsersStore.ParseLine(line), line);

    [TestMethod]
    [DataRow("only:four:parts:here")]                    // 4 parts, needs 5
    [DataRow("a:b:c:d:e:f")]                             // 6 parts
    [DataRow(":operator:100000:aGVsbG8=:aGVsbG8=")]      // empty username
    [DataRow("alice:unknown:100000:aGVsbG8=:aGVsbG8=")]  // bad role
    [DataRow("alice:operator:9999:aGVsbG8=:aGVsbG8=")]   // iterations < 10000
    [DataRow("alice:operator:abc:aGVsbG8=:aGVsbG8=")]    // non-numeric iterations
    [DataRow("alice:operator:100000:!!!bad!!!:aGVsbG8=")] // bad base64 salt
    [DataRow("alice:operator:100000:aA==:aGVsbG8=")]     // salt too short (< 8 bytes)
    [DataRow("alice:operator:100000:aGVsbG8=:YQ==")]     // hash too short (< 16 bytes)
    public void ParseLine_MalformedLine_ReturnsNull(string line) =>
        Assert.IsNull(LocalUsersStore.ParseLine(line), line);

    [TestMethod]
    public void ParseLine_ValidOperatorLine_ReturnsRecord()
    {
        var line = LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pass", 10_000);
        var record = LocalUsersStore.ParseLine(line);

        Assert.IsNotNull(record);
        Assert.AreEqual("alice", record.User);
        Assert.AreEqual(DashboardRole.Operator, record.Role);
        Assert.AreEqual(10_000, record.Iterations);
        Assert.IsTrue(record.Salt.Length >= 8);
        Assert.IsTrue(record.Hash.Length >= 16);
    }

    [TestMethod]
    public void ParseLine_ValidViewerLine_ReturnsRecord()
    {
        var line = LocalUsersStore.FormatLine("bob", DashboardRole.Viewer, "pw", 10_000);
        var record = LocalUsersStore.ParseLine(line);

        Assert.IsNotNull(record);
        Assert.AreEqual("bob", record.User);
        Assert.AreEqual(DashboardRole.Viewer, record.Role);
    }

    [TestMethod]
    public void ParseLine_LeadingAndTrailingWhitespace_IsTrimmed()
    {
        var clean = LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pass", 10_000);
        var padded = "  " + clean + "  ";
        var record = LocalUsersStore.ParseLine(padded);

        Assert.IsNotNull(record);
        Assert.AreEqual("alice", record.User);
    }

    [TestMethod]
    public void ParseLine_RoleIsCaseInsensitive()
    {
        // Operator and Viewer are case-insensitive per the parser.
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.AreEqual(DashboardRole.Operator, LocalUsersStore.ParseLine($"u:OPERATOR:10000:{salt}:{hash}")?.Role);
        Assert.AreEqual(DashboardRole.Viewer, LocalUsersStore.ParseLine($"u:VIEWER:10000:{salt}:{hash}")?.Role);
    }

    // ---------------------------------------------------------------- FormatLine validation

    [TestMethod]
    public void FormatLine_UsernameWithColon_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => LocalUsersStore.FormatLine("a:b", DashboardRole.Operator, "pw"));

    [TestMethod]
    public void FormatLine_UsernameWithHash_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => LocalUsersStore.FormatLine("a#b", DashboardRole.Operator, "pw"));

    [TestMethod]
    public void FormatLine_IterationsOutOfRange_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LocalUsersStore.FormatLine("u", DashboardRole.Operator, "pw", 9_999));
    }

    // ---------------------------------------------------------------- TryAuthenticate

    [TestMethod]
    public void TryAuthenticate_CorrectPassword_ReturnsTrue()
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "correctPw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsTrue(store.TryAuthenticate("alice", "correctPw", out var rec));
            Assert.IsNotNull(rec);
            Assert.AreEqual("alice", rec!.User);
            Assert.AreEqual(DashboardRole.Operator, rec.Role);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void TryAuthenticate_WrongPassword_ReturnsFalse()
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "correctPw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsFalse(store.TryAuthenticate("alice", "wrong", out var rec));
            Assert.IsNull(rec);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void TryAuthenticate_UnknownUser_ReturnsFalse()
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsFalse(store.TryAuthenticate("nobody", "pw", out _));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(null, "pw")]
    [DataRow("", "pw")]
    [DataRow("alice", null)]
    [DataRow("alice", "")]
    public void TryAuthenticate_NullOrEmptyInputs_ReturnsFalse(string? user, string? password)
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsFalse(store.TryAuthenticate(user, password, out _));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void TryAuthenticate_UserNameTooLong_ReturnsFalse()
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsFalse(store.TryAuthenticate(new string('a', 129), "pw", out _));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void TryAuthenticate_PasswordTooLong_ReturnsFalse()
    {
        var path = TempFile(LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw"));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.IsFalse(store.TryAuthenticate("alice", new string('x', 257), out _));
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- File loading

    [TestMethod]
    public void Store_FileNotFound_EmptyUsers()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lu-notexist-" + Guid.NewGuid().ToString("N") + ".txt");
        using var store = new LocalUsersStore(path);
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void Store_SomeBadLines_OnlyValidLinesLoaded()
    {
        var good1 = LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw1");
        var good2 = LocalUsersStore.FormatLine("bob", DashboardRole.Viewer, "pw2");
        var path = TempFile(string.Join("\n",
            good1,
            "# comment line",
            "",                                 // blank
            "bad:line",                         // wrong field count
            "x:badRole:10000:aGVsbG8=:aGVsbG8=", // bad role
            good2));
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.AreEqual(2, store.Count, "only valid lines count");
            Assert.IsTrue(store.TryAuthenticate("alice", "pw1", out _));
            Assert.IsTrue(store.TryAuthenticate("bob", "pw2", out _));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Store_DuplicateUsername_LastEntryWins()
    {
        var first = LocalUsersStore.FormatLine("alice", DashboardRole.Viewer, "pw1");
        var second = LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw2");
        var path = TempFile(first + "\n" + second);
        try
        {
            using var store = new LocalUsersStore(path);
            Assert.AreEqual(1, store.Count);
            Assert.IsTrue(store.TryAuthenticate("alice", "pw2", out var rec));
            Assert.AreEqual(DashboardRole.Operator, rec!.Role);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Store_CorruptedBase64InFile_LineIsSkipped()
    {
        var good = LocalUsersStore.FormatLine("alice", DashboardRole.Operator, "pw");
        var bad = "alice:operator:10000:!!!:!!!";
        var path = TempFile(good + "\n" + bad);
        try
        {
            using var store = new LocalUsersStore(path);
            // Only the good line is loaded; the bad base64 line is silently skipped.
            Assert.AreEqual(1, store.Count);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- helpers

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lu-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }
}
