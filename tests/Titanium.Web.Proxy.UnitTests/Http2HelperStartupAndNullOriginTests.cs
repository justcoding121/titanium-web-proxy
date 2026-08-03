using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2HelperStartupAndNullOriginTests
{
    [TestMethod]
    public async Task SendHttp2ClientConnectionStartupAsync_WritesSettingsAndWindowUpdate()
    {
        using var ms = new MemoryStream();
        await Http2Helper.SendHttp2ClientConnectionStartupAsync(ms, CancellationToken.None);

        var wire = ms.ToArray();
        // SETTINGS header (9) + 6-byte ENABLE_PUSH payload + WINDOW_UPDATE header (9) + 4-byte increment
        Assert.AreEqual(28, wire.Length, $"wire hex={Convert.ToHexString(wire)}");
        Assert.AreEqual((byte)Http2FrameType.Settings, wire[3]);
        Assert.AreEqual(6, (wire[0] << 16) | (wire[1] << 8) | wire[2]);

        // ENABLE_PUSH setting id + zero value
        var settingsId = (wire[9] << 8) | wire[10];
        Assert.AreEqual((int)Http2SettingsId.EnablePush, settingsId);
        Assert.AreEqual(0, (wire[11] << 24) | (wire[12] << 16) | (wire[13] << 8) | wire[14]);

        Assert.AreEqual((byte)Http2FrameType.WindowUpdate, wire[18]);
        var increment = (wire[24] << 24) | (wire[25] << 16) | (wire[26] << 8) | wire[27];
        Assert.AreEqual(Http2Helper.InitialConnectionWindowIncrement, increment);
    }

    [TestMethod]
    public async Task NullOriginStream_ServesEmptySettingsThenBlocksUntilCancel()
    {
        using var cts = new CancellationTokenSource();
        var stream = new NullOriginStream(cts.Token);

        var buf = new byte[16];
        var n1 = await stream.ReadAsync(buf, 0, buf.Length);
        Assert.AreEqual(9, n1);
        Assert.AreEqual((byte)Http2FrameType.Settings, buf[3]);
        Assert.AreEqual(0, (buf[0] << 16) | (buf[1] << 8) | buf[2]);

        // Writes are discarded
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await stream.FlushAsync();

        var blocked = stream.ReadAsync(buf, 0, buf.Length);
        Assert.IsFalse(blocked.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await blocked);
    }
}
