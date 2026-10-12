using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionMarkTests
{
    [TestMethod]
    public void Comment_IsCappedAt2000()
    {
        var snap = new SessionSnapshot { Comment = new string('a', 2001) };
        Assert.AreEqual(2000, snap.Comment!.Length);
        snap.Comment = "";
        Assert.IsNull(snap.Comment);
    }

    [TestMethod]
    public void Search_MatchesMarkAndComment()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, Url = "https://a/", Mark = SessionMark.Red, Comment = "follow up" },
            new SessionSnapshot { Id = 2, Url = "https://a/", Mark = SessionMark.None, Comment = "=cmd" },
        };

        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:red").Single().Id);
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:RED").Single().Id);
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "mark:none").Single().Id);
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "comment:follow").Single().Id);
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "comment:=cmd").Single().Id);
        Assert.AreEqual(0, SessionSearch.Filter(sessions, "mark:blue").Count());
    }

    [TestMethod]
    public void Har_RoundTripsCommentAndMark()
    {
        var snap = new SessionSnapshot
        {
            Id = 4,
            Method = "GET",
            Url = "https://example.com/m",
            StatusCode = 200,
            Mark = SessionMark.Purple,
            Comment = "note",
        };

        var entry = SessionArchive.ToHarEntry(snap);
        Assert.AreEqual("note", entry["comment"]!.GetValue<string>());
        using var doc = JsonDocument.Parse(entry.ToJsonString());
        var imported = SessionArchive.FromHarEntry(doc.RootElement, 9);
        Assert.IsNotNull(imported);
        Assert.AreEqual(SessionMark.Purple, imported!.Mark);
        Assert.AreEqual("note", imported.Comment);
    }

    [TestMethod]
    public void SetMark_AndComment_UpdateTheSessionAndRefilter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-mark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var dialogs = new ScriptedInspectorDialogs { CommentResult = "saved" };
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            dialogs,
            new ScriptedInspectorPathPicker()));

        var snap = new SessionSnapshot { Id = 1, Url = "https://a/", StatusCode = 200 };
        registry.Store.Sessions.Add(snap);
        vm.SelectedSession = snap;
        vm.SetMarkCommand.Execute("Green");
        Assert.AreEqual(SessionMark.Green, snap.Mark);

        vm.CommentCommand.Execute(null);
        Assert.AreEqual(1, dialogs.CommentCalls);
        Assert.AreEqual("saved", snap.Comment);
    }
}
