using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Quic.UdpOffload;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicUdpOffloadTests
{
    [TestMethod]
    public async Task ABatchArrivesAsSeparateDatagrams()
    {
        using var left = new UdpSocketTransport();
        using var right = new UdpSocketTransport();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReadOnlyMemory<byte>[] payloads = [new byte[] { 9, 8 }, new byte[] { 7 }, new byte[] { 6, 5, 4 }];
        var buffers = new byte[3][];
        var pending = new ValueTask<UdpReceived>[3];
        for (int i = 0; i < payloads.Length; i++)
        {
            buffers[i] = new byte[16];
            pending[i] = right.ReceiveAsync(buffers[i], cancel.Token);
        }

        int sent = await left.SendBatchAsync(payloads, right.LocalEndpoint, cancel.Token);
        Assert.AreEqual(6, sent);
        for (int i = 0; i < payloads.Length; i++)
        {
            UdpReceived received = await pending[i];
            Assert.AreEqual(payloads[i].Length, received.Count);
            CollectionAssert.AreEqual(payloads[i].ToArray(), buffers[i].AsSpan(0, received.Count).ToArray());
        }
    }

    [TestMethod]
    public void LinuxEnablesTheBatchAfterTheProbe()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.IsFalse(UdpOffloadBatch.IsEnabled);
            return;
        }

        Assert.IsTrue(UdpOffloadBatch.IsEnabled);
    }

    [TestMethod]
    public void OffloadImportsAreTheSocketBatchCalls()
    {
        Assembly assembly = typeof(UdpOffloadBatch).Assembly;
        var found = new List<string>();
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                {
                    continue;
                }

                string name = method.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>()?.EntryPoint ?? method.Name;
                found.Add(name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        CollectionAssert.AreEqual(new[] { "recvmmsg", "sendmmsg" }, found.ToArray());
        foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
        {
            string name = referenced.Name ?? string.Empty;
            Assert.IsFalse(name.Contains("Cryptography", StringComparison.Ordinal), name);
            Assert.IsFalse(name.Contains("Process", StringComparison.Ordinal), name);
        }
    }
}
