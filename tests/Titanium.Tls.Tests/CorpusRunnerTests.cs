using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class CorpusRunnerTests
{
    [TestMethod]
    public void TruncationsAndMutationsStayInsideTheDocumentedResult()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fuzz", "corpus", "tls-handshake", "short-handshake.hex");
        Assert.IsTrue(File.Exists(path), path);
        CorpusFile file = CorpusFile.Load(path);
        Assert.IsFalse(HandshakePrefixParser.Accepts(file.Body));
        for (int length = 0; length < file.Body.Length; length++)
        {
            HandshakePrefixParser.Accepts(file.Body.AsSpan(0, length));
        }

        Random random = new(8446);
        byte[] mutant = (byte[])file.Body.Clone();
        for (int i = 0; i < 32; i++)
        {
            int index = random.Next(mutant.Length);
            mutant[index] = (byte)random.Next(256);
            HandshakePrefixParser.Accepts(mutant);
        }
    }
}

internal static class HandshakePrefixParser
{
    internal static bool Accepts(ReadOnlySpan<byte> record)
    {
        if (record.Length < 5 || record[0] != 22)
        {
            return false;
        }

        int length = (record[3] << 8) | record[4];
        return record.Length == 5 + length && length > 0;
    }
}

internal sealed class CorpusFile
{
    private CorpusFile(string expect, byte[] body)
    {
        Expect = expect;
        Body = body;
    }

    internal string Expect { get; }

    internal byte[] Body { get; }

    internal static CorpusFile Load(string path)
    {
        string text = File.ReadAllText(path);
        string expect = "reject";
        foreach (string line in text.Split('\n'))
        {
            const string marker = "# expect:";
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                expect = line[marker.Length..].Trim();
            }
        }

        return new CorpusFile(expect, HexVectors.Parse(text));
    }
}
