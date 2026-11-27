using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class EngineTests
{
    [TestMethod]
    public void StubHandshakeReachesConnected()
    {
        var client = new TlsEngine(TlsRole.Client);
        var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out int consumed, output);
        Assert.AreEqual(0, consumed);
        byte[] hello = output.Bytes.ToArray();
        Assert.AreEqual(22, hello[0]);
        Assert.AreEqual(1, hello[5]);

        server.Advance(hello, out consumed, output);
        Assert.AreEqual(hello.Length, consumed);
        Assert.AreEqual(TlsEventKind.ClientHelloReceived, output.Event(0).Kind);
        Assert.AreEqual(TlsEngineState.WaitClientHelloDecision, server.State);

        server.Resume(true, output);
        byte[] flight = output.Bytes.ToArray();
        Assert.AreEqual(TlsEngineState.WaitFinished, server.State);

        client.Advance(flight, out consumed, output);
        Assert.AreEqual(flight.Length, consumed);
        Assert.AreEqual(TlsEventKind.ServerCertificateReceived, output.Event(0).Kind);
        Assert.AreEqual(TlsEngineState.WaitCertificateDecision, client.State);

        client.Resume(true, output);
        Assert.AreEqual(TlsEventKind.HandshakeCompleted, output.Event(0).Kind);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        byte[] finished = output.Bytes.ToArray();
        server.Advance(finished, out _, output);
        Assert.AreEqual(TlsEventKind.HandshakeCompleted, output.Event(0).Kind);
        Assert.AreEqual(TlsEngineState.Connected, server.State);

        client.Advance(ApplicationData(), out _, output);
        Assert.AreEqual(0, output.Bytes.Length);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
    }

    [TestMethod]
    public void IllegalTransitionSendsUnexpectedMessage()
    {
        var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        client.Advance(HandshakeRecord(11), out _, output);
        AssertAlert(output, (byte)TlsRecordAlert.UnexpectedMessage);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void ClosedEngineRejectsFurtherInput()
    {
        var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        client.Advance(HandshakeRecord(11), out _, output);
        client.Advance(HandshakeRecord(2), out int consumed, output);
        Assert.AreEqual(0, consumed);
        Assert.AreEqual(0, output.Bytes.Length);
        Assert.AreEqual(0, output.EventCount);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void OversizedHandshakeMessageIsRejected()
    {
        var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        var record = new byte[9];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        record[4] = 4;
        record[5] = 2;
        record[6] = 2;
        record[8] = 1;
        client.Advance(record, out _, output);
        AssertAlert(output, (byte)HandshakeAlert.DecodeError);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void TooManyEmptyRecordsAreRejected()
    {
        var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] empty = [23, 3, 3, 0, 0];
        for (int i = 0; i < TlsEngine.MaxEmptyRecords; i++)
        {
            client.Advance(empty, out int consumed, output);
            Assert.AreEqual(empty.Length, consumed);
            Assert.AreEqual(TlsEngineState.WaitServerHello, client.State);
        }

        client.Advance(empty, out _, output);
        AssertAlert(output, (byte)TlsRecordAlert.UnexpectedMessage);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void BufferedByteCapIsEnforced()
    {
        var client = new TlsEngine(TlsRole.Client, maxBufferedBytes: 20);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        var record = new byte[21];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        record[4] = 16;
        client.Advance(record, out _, output);
        AssertAlert(output, (byte)TlsRecordAlert.RecordOverflow);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void EngineMethodsDoNotReturnTasks()
    {
        MethodInfo[] methods = typeof(TlsEngine).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.IsTrue(methods.Length > 0);
        foreach (MethodInfo method in methods)
        {
            Assert.IsFalse(typeof(Task).IsAssignableFrom(method.ReturnType), method.Name);
            Assert.IsFalse(method.ReturnType.IsGenericType && method.ReturnType.Name.StartsWith("ValueTask", StringComparison.Ordinal), method.Name);
        }
    }

    [TestMethod]
    public void CryptoExecutionPolicyStaysInlineUntilValidation()
    {
        Assert.AreEqual(CryptoOffloadReason.None, CryptoExecutionPolicy.Revocation(false));
        Assert.AreEqual(CryptoOffloadReason.OnlineRevocation, CryptoExecutionPolicy.Revocation(true));
        Assert.AreEqual(CryptoOffloadReason.None, CryptoExecutionPolicy.Signing(true));
        Assert.AreEqual(CryptoOffloadReason.ExternalPrivateKey, CryptoExecutionPolicy.Signing(false));
        Assert.AreEqual(Environment.ProcessorCount * 4, CryptoExecutionPolicy.HandshakeConcurrencyLimit(Environment.ProcessorCount));
        int calls = 0;
        CryptoExecutionPolicy.Run(CryptoOffloadReason.OnlineRevocation, () => calls++);
        Assert.AreEqual(1, calls);
    }

    private static byte[] HandshakeRecord(byte type)
    {
        return [22, 3, 3, 0, 4, type, 0, 0, 0];
    }

    private static byte[] ApplicationData()
    {
        return [23, 3, 3, 0, 1, 0];
    }

    private static void AssertAlert(TlsOutput output, byte description)
    {
        Assert.AreEqual(7, output.Bytes.Length);
        Assert.AreEqual(21, output.Bytes[0]);
        Assert.AreEqual(2, output.Bytes[5]);
        Assert.AreEqual(description, output.Bytes[6]);
    }
}
