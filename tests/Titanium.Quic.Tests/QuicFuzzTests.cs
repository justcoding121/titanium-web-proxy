using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicFuzzTests
{
    private static readonly byte[][] PacketSeeds =
    [
        [0x80],
        [0xC0, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00],
        [0x40, 0x01, 0x02, 0x03, 0x04],
        [0x00],
    ];

    private static readonly byte[][] FrameSeeds =
    [
        [0x00],
        [0x01],
        [0x02, 0x00, 0x00, 0x00, 0x00],
        [0x06, 0x00, 0x00, 0x00],
        [0xFF],
    ];

    [TestMethod]
    public void PacketAndFrameSeedsDoNotThrow()
    {
        var random = new Random(9000);
        foreach (byte[] seed in PacketSeeds)
        {
            Exercise(seed);
            for (int length = 0; length <= seed.Length; length++)
            {
                Exercise(seed.AsSpan(0, length).ToArray());
            }

            for (int i = 0; i < 16; i++)
            {
                byte[] mutant = (byte[])seed.Clone();
                mutant[random.Next(mutant.Length)] ^= (byte)(1 << random.Next(8));
                Exercise(mutant);
            }
        }

        foreach (byte[] seed in FrameSeeds)
        {
            ExerciseFrames(seed);
            byte[] mutant = (byte[])seed.Clone();
            mutant[0] ^= 0x7F;
            ExerciseFrames(mutant);
        }
    }

    private static void Exercise(byte[] datagram)
    {
        _ = QuicDatagramReader.TryParse(datagram, 8, out _);
        ExerciseFrames(datagram);
    }

    private static void ExerciseFrames(byte[] payload)
    {
        var reader = new QuicFrameReader(payload, QuicPacketKind.OneRtt);
        var ranges = new QuicAckRange[32];
        while (reader.TryNext(ranges, out _))
        {
        }
    }
}
