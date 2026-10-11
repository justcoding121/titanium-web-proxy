using System.Collections;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionCopyTests
{
    [TestMethod]
    public void PowerShell_PullsContentTypeAndUserAgent_AndDoublesQuotes()
    {
        var snap = Awkward();
        var text = SessionRequestCodegen.ToPowerShell(snap);
        StringAssert.Contains(text, "-ContentType 'application/json'");
        StringAssert.Contains(text, "-UserAgent 'a''b'");
        StringAssert.Contains(text, "-UseBasicParsing");
        StringAssert.Contains(text, "-Body 'line\r\nquote''x'");
        var headersAt = text.IndexOf("-Headers", StringComparison.Ordinal);
        Assert.IsTrue(headersAt > 0);
        var headers = text[headersAt..];
        Assert.IsFalse(headers.Contains("Content-Type", StringComparison.Ordinal));
        Assert.IsFalse(headers.Contains("User-Agent", StringComparison.Ordinal));
        StringAssert.Contains(headers, "'X-Trace' = 'say ''hi'''");
    }

    [TestMethod]
    public void Python_EscapesQuotesAndNewlines_AndUsesBytesForBinary()
    {
        var text = SessionRequestCodegen.ToPython(Awkward());
        StringAssert.StartsWith(text, "import requests\nrequests.request(");
        StringAssert.Contains(text, "'POST'");
        StringAssert.Contains(text, "data='line\\r\\nquote\\'x'");
        StringAssert.Contains(text, "'X-Trace': 'say \\'hi\\''");

        var binary = new SessionSnapshot
        {
            Method = "PUT",
            Url = "https://ex/b",
            RequestBodyBytes = [0, 1, 255, (byte)'\'', (byte)'A'],
        };
        var py = SessionRequestCodegen.ToPython(binary);
        StringAssert.Contains(py, "data=b'\\x00\\x01\\xff\\'A'");
    }

    [TestMethod]
    public void Python_IncludesALargeBody()
    {
        var body = new string('Q', 200_000);
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://ex/big",
            RequestBodyText = body,
        };
        var text = SessionRequestCodegen.ToPython(snap);
        StringAssert.Contains(text, "data='" + body + "'");
    }

    [TestMethod]
    public void Table_NeutralizesFormulas_AndCapsRows()
    {
        var rows = new[]
        {
            new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                StatusCode = 200,
                Url = "https://a/\tpath",
                Comment = "=cmd",
            },
            new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                StatusCode = 200,
                Url = "https://b/",
                Comment = "+1\n@x",
            },
        };
        var table = SessionRequestCodegen.ToTable(rows, out var written, out var truncated);
        Assert.IsFalse(truncated);
        Assert.AreEqual(2, written);
        StringAssert.Contains(table, "'=cmd");
        StringAssert.Contains(table, "https://a/ path");
        StringAssert.Contains(table, "'+1 @x");
        Assert.IsFalse(table.Contains("\tpath", StringComparison.Ordinal));

        var many = new RepeatList(SessionRequestCodegen.MaxTableRows + 1, rows[0]);
        SessionRequestCodegen.ToTable(many, out var capped, out var wasCapped);
        Assert.IsTrue(wasCapped);
        Assert.AreEqual(SessionRequestCodegen.MaxTableRows, capped);
    }

    [TestMethod]
    public void RawHttp_WritesRequestAndResponse_AndCutsALongBody()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://ex/v1?x=1",
            StatusCode = 201,
            RequestHeadersText = "Content-Type: text/plain\n",
            ResponseHeadersText = "X-Ok: 1\r\n",
            RequestBodyText = "hello-world",
            ResponseBodyText = "created",
        };
        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 7);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.StartsWith(text, "POST /v1?x=1 HTTP/1.1\r\n");
        StringAssert.Contains(text, "Host: ex\r\n");
        StringAssert.Contains(text, "Content-Type: text/plain\r\n");
        StringAssert.Contains(text, "hello-w");
        Assert.IsFalse(text.Contains("hello-world", StringComparison.Ordinal));
        StringAssert.Contains(text, "HTTP/1.1 201 Created\r\n");
        StringAssert.Contains(text, "created");
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public void RawHttp_StreamsSpilledBodies_WithoutLoadingThemBack()
    {
        var snap = new SessionSnapshot
        {
            Id = 4,
            Method = "POST",
            Url = "https://ex/z",
            StatusCode = 200,
            DurationMs = 3,
            RequestHeadersText = "Content-Type: text/plain\r\n",
            RequestBodyText = "req-body",
            ResponseBodyBytes = [1, 2, 3, 4],
            ResponseHeadersText = "Content-Type: application/octet-stream\r\n",
        };
        using var har = new MemoryStream();
        SessionArchive.WriteHarDocument(har, snap);
        snap.RequestBodyText = null;
        snap.ResponseBodyBytes = null;
        snap.BodiesOnDisk = true;

        using var output = new MemoryStream();
        har.Position = 0;
        var result = SessionRawHttp.Write(snap, output, har);
        var bytes = output.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(text, "req-body");
        CollectionAssert.Contains(bytes, (byte)1);
        Assert.IsTrue(bytes.AsSpan().IndexOf(new byte[] { 1, 2, 3, 4 }) >= 0);
        Assert.IsNull(snap.RequestBodyText);
        Assert.IsNull(snap.ResponseBodyBytes);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RawHttp_SpilledFile_IsReadWithoutRestoringTheSnapshot()
    {
        var cache = Path.Combine(Path.GetTempPath(), "ti-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        using var registry = new SessionRegistry(
            new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 20 },
            cache);
        var snap = new SessionSnapshot
        {
            Id = 11,
            Method = "POST",
            Url = "https://ex/disk",
            StatusCode = 200,
            DurationMs = 2,
            RequestBodyText = "from-disk",
            ResponseBodyText = "resp-disk",
        };
        registry.Add(snap);
        var opened = false;
        FileStream? har = null;
        for (var i = 0; i < 40 && !opened; i++)
        {
            opened = registry.Store.TryOpenSpilledHar(snap, out har);
            if (!opened)
            {
                await Task.Delay(50);
            }
        }

        Assert.IsTrue(opened, "spill file was not opened");
        using (har)
        {
            Assert.IsNull(snap.RequestBodyText);
            using var output = new MemoryStream();
            SessionRawHttp.Write(snap, output, har);
            var text = Encoding.UTF8.GetString(output.ToArray());
            StringAssert.Contains(text, "from-disk");
            StringAssert.Contains(text, "resp-disk");
        }

        Assert.IsNull(snap.RequestBodyText);
        Assert.IsNull(snap.ResponseBodyText);
    }

    [TestMethod]
    public void ViewModel_BuildsTheSamePowerShell_AsTheGenerator()
    {
        var (vm, picker) = NewVm();
        var snap = Awkward();
        snap.Id = 1;
        vm.SeedSession(snap);
        vm.SelectedSession = snap;
        Assert.IsTrue(vm.TryBuildCopyAsPowerShell(out var text));
        Assert.AreEqual(SessionRequestCodegen.ToPowerShell(snap), text);
        vm.SelectedSession = new SessionSnapshot { Id = 2, Url = "https://ex/", IsTunnel = true };
        Assert.IsFalse(vm.TryBuildCopyAsPowerShell(out _));
        picker.SavePath = Path.Combine(Path.GetTempPath(), "ti-raw-" + Guid.NewGuid().ToString("N") + ".http");
        vm.SelectedSession = snap;
        vm.SaveRawHttpCommand.Execute(null);
        var deadline = Environment.TickCount64 + 5000;
        string? saved = null;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (File.Exists(picker.SavePath))
                {
                    saved = File.ReadAllText(picker.SavePath);
                    if (saved.Contains("quote'x", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
                saved = null;
            }

            Thread.Sleep(20);
        }

        Assert.IsNotNull(saved);
        StringAssert.Contains(saved, "quote'x");
    }

    private static SessionSnapshot Awkward() => new()
    {
        Method = "POST",
        Url = "https://ex/v1",
        RequestHeadersText =
            "Content-Type: application/json\r\n" +
            "User-Agent: a'b\r\n" +
            "X-Trace: say 'hi'\r\n" +
            "Host: ex\r\n" +
            "Content-Length: 9\r\n",
        RequestBodyText = "line\r\nquote'x",
    };

    private static (MainWindowViewModel Vm, ScriptedInspectorPathPicker Picker) NewVm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var picker = new ScriptedInspectorPathPicker();
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            new ScriptedInspectorDialogs(),
            picker));
        return (vm, picker);
    }

    private sealed class RepeatList(int count, SessionSnapshot row) : IReadOnlyList<SessionSnapshot>
    {
        public int Count { get; } = count;
        public SessionSnapshot this[int index] => row;
        public IEnumerator<SessionSnapshot> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return row;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
