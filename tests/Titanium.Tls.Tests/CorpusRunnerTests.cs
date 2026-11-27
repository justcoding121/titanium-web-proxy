using System;
using System.Collections.Generic;
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
        string directory = Path.Combine(AppContext.BaseDirectory, "fuzz", "corpus", "tls-handshake");
        string[] files = Directory.GetFiles(directory, "*.hex");
        Assert.IsTrue(files.Length > 0, directory);
        var types = new HashSet<byte>();
        var random = new Random(8446);
        foreach (string file in files)
        {
            CorpusFile seed = CorpusFile.Load(file);
            if (seed.Body.Length > 5 && seed.Body[0] == 22)
            {
                types.Add(seed.Body[5]);
            }

            TlsFuzzTarget.Handshake(seed.Body);
            for (int length = 0; length < seed.Body.Length; length++)
            {
                TlsFuzzTarget.Handshake(seed.Body.AsSpan(0, length).ToArray());
            }

            if (seed.Body.Length == 0)
            {
                continue;
            }

            byte[] mutant = (byte[])seed.Body.Clone();
            for (int i = 0; i < 16; i++)
            {
                mutant[random.Next(mutant.Length)] = (byte)random.Next(256);
                TlsFuzzTarget.Handshake(mutant);
            }
        }

        byte[] required = [1, 2, 4, 5, 8, 11, 13, 15, 20, 24];
        foreach (byte type in required)
        {
            Assert.IsTrue(types.Contains(type), type.ToString());
        }
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
