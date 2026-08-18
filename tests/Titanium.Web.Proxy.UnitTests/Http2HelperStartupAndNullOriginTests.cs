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
        // SETTINGS header (9) + 18-byte payload (HEADER_TABLE_SIZE=0, ENABLE_PUSH=0,
        // INITIAL_WINDOW_SIZE=reference .NET server stack-class) + WINDOW_UPDATE header (9) + 4-byte increment
        Assert.AreEqual(40, wire.Length, $"wire hex={Convert.ToHexString(wire)}");
        Assert.AreEqual((byte)Http2FrameType.Settings, wire[3]);
        Assert.AreEqual(18, (wire[0] << 16) | (wire[1] << 8) | wire[2]);

        var headerTableId = (wire[9] << 8) | wire[10];
        Assert.AreEqual((int)Http2SettingsId.HeaderTableSize, headerTableId);
        Assert.AreEqual(0, (wire[11] << 24) | (wire[12] << 16) | (wire[13] << 8) | wire[14]);

        var enablePushId = (wire[15] << 8) | wire[16];
        Assert.AreEqual((int)Http2SettingsId.EnablePush, enablePushId);
        Assert.AreEqual(0, (wire[17] << 24) | (wire[18] << 16) | (wire[19] << 8) | wire[20]);

        var initialWindowId = (wire[21] << 8) | wire[22];
        Assert.AreEqual((int)Http2SettingsId.InitialWindowSize, initialWindowId);
        Assert.AreEqual(Http2Helper.ClientInitialStreamWindowSize,
            (wire[23] << 24) | (wire[24] << 16) | (wire[25] << 8) | wire[26]);

        Assert.AreEqual((byte)Http2FrameType.WindowUpdate, wire[30]);
        var increment = (wire[36] << 24) | (wire[37] << 16) | (wire[38] << 8) | wire[39];
        Assert.AreEqual(Http2Helper.InitialConnectionWindowIncrement, increment);
    }

    [TestMethod]
    public async Task NullOriginStream_ServesEmptySettingsThenBlocksUntilCancel()
    {
        using var cts = new CancellationTokenSource();
        var stream = new NullOriginStream(cts.Token);

        var buf = new byte[32];
        var n1 = await stream.ReadAsync(buf);
        Assert.AreEqual(21, n1); // 9-byte header + 12-byte SETTINGS payload
        Assert.AreEqual((byte)Http2FrameType.Settings, buf[3]);
        Assert.AreEqual(12, (buf[0] << 16) | (buf[1] << 8) | buf[2]);

        // Writes are discarded
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await stream.FlushAsync();

        var blocked = stream.ReadAsync(buf);
        Assert.IsFalse(blocked.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await blocked);
    }
}
