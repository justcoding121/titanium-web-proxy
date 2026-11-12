using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Updates;

namespace Titanium.Cli.Tests;

/// <summary>
///     Plus bundle install edges that protect an existing install: partial swaps must roll back, hostile or malformed
///     bundles must be refused before anything is touched, and removal must stay inside the install directory.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PlusBundleApplyEdgeBehaviorTests
{
    private string _root = null!;
    private string _install = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "twp-plus-edge-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // Bug class: a legacy single-dll update leaves no backup (cannot roll back) or leaves a .new file behind.
    [TestMethod]
    public void LegacyDll_ReplacesTheInstalledDllKeepsABackupAndLeavesNoStagingFile()
    {
        var dest = Path.Combine(_install, "Titanium.Plus.dll");
        File.WriteAllText(dest, "old");
        var source = Path.Combine(_root, "Titanium.Plus.dll");
        File.WriteAllText(source, "new");

        Assert.AreEqual(0, UpdateCommand.ApplyPlusLegacyDll(source, _install));

        Assert.AreEqual("new", File.ReadAllText(dest));
        Assert.AreEqual("old", File.ReadAllText(dest + ".bak"));
        Assert.IsFalse(File.Exists(dest + ".new"));
    }

    [TestMethod]
    public void LegacyDll_FirstInstall_NeedsNoBackup()
    {
        var source = Path.Combine(_root, "Titanium.Plus.dll");
        File.WriteAllText(source, "fresh");

        Assert.AreEqual(0, UpdateCommand.ApplyPlusLegacyDll(source, _install));

        Assert.AreEqual("fresh", File.ReadAllText(Path.Combine(_install, "Titanium.Plus.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(_install, "Titanium.Plus.dll.bak")));
    }

    // Bug class: a failed legacy update (source gone) corrupts or deletes the working Plus dll and reports success.
    [TestMethod]
    public async Task LegacyDll_MissingSource_FailsKeepsTheInstalledDllAndSaysWhy()
    {
        var dest = Path.Combine(_install, "Titanium.Plus.dll");
        File.WriteAllText(dest, "working");

        var (code, stderr) = await CaptureStderrAsync(
            () => UpdateCommand.ApplyPlusLegacyDll(Path.Combine(_root, "absent.dll"), _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Plus update failed");
        Assert.AreEqual("working", File.ReadAllText(dest));
        Assert.IsFalse(File.Exists(dest + ".new"));
    }

    // Bug class: a swap that fails half way leaves a mix of old and new Plus files (loads, then crashes at runtime).
    [TestMethod]
    public async Task BundleSwapFailingOnALaterFile_RestoresEveryEarlierFile()
    {
        File.WriteAllText(Path.Combine(_install, "Titanium.Plus.dll"), "old-dll");
        // A directory squatting on the last file name makes that swap fail after the first two succeeded.
        Directory.CreateDirectory(Path.Combine(_install, "Zeta.dll"));
        var zip = WriteZip(
            ("Titanium.Plus.dll", "new-dll"),
            ("Zeta.dll", "new-zeta"),
            ("Titanium.Plus.files", "Titanium.Plus.dll\nZeta.dll\nTitanium.Plus.files\n"));

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual(1, code, stderr);
        StringAssert.Contains(stderr, "Plus update failed");
        Assert.AreEqual("old-dll", File.ReadAllText(Path.Combine(_install, "Titanium.Plus.dll")), "the swapped file must be restored");
        Assert.IsFalse(File.Exists(Path.Combine(_install, "Titanium.Plus.files")), "a file that did not exist before must be removed again");
        Assert.AreEqual(0, Directory.GetDirectories(_install, ".plus-stage-*").Length, "the stage directory must be cleaned up");
    }

    // Bug class: a bundle that is not a zip at all crashes the updater with a raw exception instead of being rejected.
    [TestMethod]
    public async Task BundleThatIsNotAZip_IsRejectedWithoutTouchingTheInstall()
    {
        File.WriteAllText(Path.Combine(_install, "Titanium.Plus.dll"), "old");
        var notZip = Path.Combine(_root, "bundle.zip");
        File.WriteAllText(notZip, "this is not a zip archive");

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(notZip, _install));

        Assert.AreEqual(1, code);
        Assert.IsTrue(stderr.Contains("Plus bundle rejected", StringComparison.Ordinal) || stderr.Contains("Plus update failed", StringComparison.Ordinal), stderr);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_install, "Titanium.Plus.dll")));
    }

    [TestMethod]
    public async Task BundleFileThatDoesNotExist_FailsCleanly()
    {
        var (code, stderr) = await CaptureStderrAsync(
            () => UpdateCommand.ApplyPlusBundleZip(Path.Combine(_root, "absent.zip"), _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "Plus update failed");
        Assert.AreEqual(0, Directory.GetDirectories(_install, ".plus-stage-*").Length);
    }

    // Bug class: a bundle without its file list installs unlisted files that remove-plus can never delete.
    [TestMethod]
    public async Task BundleWithoutTheFileList_IsRejected()
    {
        var zip = WriteZip(("Titanium.Plus.dll", "x"));

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "missing Titanium.Plus.files");
        Assert.IsFalse(File.Exists(Path.Combine(_install, "Titanium.Plus.dll")));
    }

    // Bug class: the file list and the archive disagree (extra unlisted file smuggled in, or listed file missing).
    [TestMethod]
    public async Task BundleWhoseFileListDisagreesWithTheArchive_IsRejected()
    {
        var zip = WriteZip(
            ("Titanium.Plus.dll", "x"),
            ("Extra.dll", "smuggled"),
            ("Titanium.Plus.files", "Titanium.Plus.dll\nTitanium.Plus.files\n"));

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "does not match the archive");
        Assert.IsFalse(File.Exists(Path.Combine(_install, "Extra.dll")));
    }

    // Bug class: a file list that omits itself, repeats a name, or names a path breaks removal or escapes the install.
    [TestMethod]
    [DataRow("Titanium.Plus.dll\nTitanium.Plus.dll\nTitanium.Plus.files\n", "duplicated")]
    [DataRow("Titanium.Plus.dll\n", "must include Titanium.Plus.files")]
    [DataRow("..\\evil.dll\nTitanium.Plus.files\n", "not a plain file name")]
    public async Task BundleWithABadFileList_IsRejected(string list, string expected)
    {
        var zip = WriteZip(("Titanium.Plus.dll", "x"), ("Titanium.Plus.files", list));

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, expected);
        Assert.IsFalse(File.Exists(Path.Combine(_install, "Titanium.Plus.dll")));
    }

    // Bug class: zip directory entries or blank list lines are treated as files and fail an otherwise valid bundle.
    [TestMethod]
    public void BundleWithDirectoryEntriesAndBlankListLines_InstallsTheListedFiles()
    {
        var zip = Path.Combine(_root, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("docs/");
            archive.CreateEntry("more\\");
            AddEntry(archive, "Titanium.Plus.dll", "payload");
            AddEntry(archive, "Titanium.Plus.files", "\n  \nTitanium.Plus.dll\n\nTitanium.Plus.files\n");
        }

        Assert.AreEqual(0, UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(_install, "Titanium.Plus.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(_install, "Titanium.Plus.files")));
    }

    // Bug class: a nested path in the zip is extracted outside the stage directory (zip slip).
    [TestMethod]
    public async Task BundleEntryWithATraversalPath_IsRejectedAndNothingEscapes()
    {
        var zip = WriteZip(
            ("../escaped.dll", "evil"),
            ("Titanium.Plus.files", "Titanium.Plus.files\n"));

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.ApplyPlusBundleZip(zip, _install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "not a plain file name");
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escaped.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(_install, "escaped.dll")));
    }

    // Bug class: the plain-file-name guard accepts names the file system treats specially.
    [TestMethod]
    [DataRow("", false)]
    [DataRow(".", false)]
    [DataRow("..", false)]
    [DataRow("a..b.dll", false)]
    [DataRow("bad\u0001name.dll", false)]
    [DataRow("a:b.dll", false)]
    [DataRow("Titanium.Plus.dll", true)]
    public void IsPlainPlusFileName_RejectsSpecialNames(string name, bool expected)
    {
        Assert.AreEqual(expected, UpdateCommand.IsPlainPlusFileName(name));
    }

    [TestMethod]
    public void IsPlainPlusFileName_RejectsOverlongNames()
    {
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName(new string('a', 181)));
        Assert.IsTrue(UpdateCommand.IsPlainPlusFileName(new string('a', 180)));
    }

    // Bug class: inventory limits (empty, count, size, duplicates, bad sizes) are not enforced before extraction.
    [TestMethod]
    public void BundleInventory_RejectsEmptyNegativeDuplicateAndOversizedBundles()
    {
        var empty = Assert.ThrowsExactly<InvalidDataException>(() => UpdateCommand.EnsurePlusBundleInventory([]));
        StringAssert.Contains(empty.Message, "empty");

        var negative = Assert.ThrowsExactly<InvalidDataException>(
            () => UpdateCommand.EnsurePlusBundleInventory([("a.dll", -1L)]));
        StringAssert.Contains(negative.Message, "invalid size");

        var duplicate = Assert.ThrowsExactly<InvalidDataException>(
            () => UpdateCommand.EnsurePlusBundleInventory([("a.dll", 1L), ("a.dll", 1L)]));
        StringAssert.Contains(duplicate.Message, "duplicated");

        var oversized = Assert.ThrowsExactly<InvalidDataException>(
            () => UpdateCommand.EnsurePlusBundleInventory(
                [("a.dll", UpdateCommand.MaxPlusBundleUncompressedBytes), ("b.dll", 1L)]));
        StringAssert.Contains(oversized.Message, "uncompressed");

        UpdateCommand.EnsurePlusBundleInventory([("a.dll", UpdateCommand.MaxPlusBundleUncompressedBytes)]);
    }

    // Bug class: remove-plus follows a hostile list entry out of the install directory, or leaves its own list behind.
    [TestMethod]
    public async Task RemovePlus_ListWithAPathEntry_RefusesAndDeletesNothing()
    {
        var plus = Path.Combine(_install, "Titanium.Plus.dll");
        File.WriteAllText(plus, "keep");
        File.WriteAllText(Path.Combine(_install, UpdateCommand.PlusFileListName), "Titanium.Plus.dll\n..\\outside.dll\n");

        var (code, stderr) = await CaptureStderrAsync(() => UpdateCommand.RemovePlus(_install));

        Assert.AreEqual(1, code);
        StringAssert.Contains(stderr, "not a plain file name");
        Assert.IsTrue(File.Exists(plus), "a refused removal must not delete the files listed before the bad entry");
    }

    [TestMethod]
    public void RemovePlus_ListThatDoesNotNameItself_StillRemovesTheListFileAndBackups()
    {
        var plus = Path.Combine(_install, "Titanium.Plus.dll");
        var list = Path.Combine(_install, UpdateCommand.PlusFileListName);
        File.WriteAllText(plus, "x");
        File.WriteAllText(plus + ".bak", "x");
        File.WriteAllText(plus + ".new", "x");
        File.WriteAllText(list, "\nTitanium.Plus.dll\n\n");
        File.WriteAllText(Path.Combine(_install, "unrelated.txt"), "keep");

        Assert.AreEqual(0, UpdateCommand.RemovePlus(_install));

        Assert.IsFalse(File.Exists(plus));
        Assert.IsFalse(File.Exists(plus + ".bak"));
        Assert.IsFalse(File.Exists(plus + ".new"));
        Assert.IsFalse(File.Exists(list), "the list file must not outlive the files it described");
        Assert.IsTrue(File.Exists(Path.Combine(_install, "unrelated.txt")), "files outside the list must be left alone");
    }

    private string WriteZip(params (string Name, string Content)[] entries)
    {
        var zip = Path.Combine(_root, "bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            AddEntry(archive, name, content);
        }

        return zip;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<(int Code, string Stderr)> CaptureStderrAsync(Func<int> action)
    {
        var buffer = new StringWriter();
        var writer = TextWriter.Synchronized(buffer);
        var previous = Console.Error;
        try
        {
            Console.SetError(writer);
            var code = action();
            await AsyncConsole.FlushAsync();
            lock (writer)
            {
                return (code, buffer.ToString());
            }
        }
        finally
        {
            Console.SetError(previous);
        }
    }
}
