using System;
using System.Buffers;
using System.Collections.Generic;

namespace Titanium.Tls;

internal static class ClientHelloBuilder
{
    internal static void Write(ClientHelloTemplate template, ClientHelloContext context, IEntropySource entropy, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entropy);
        ArgumentNullException.ThrowIfNull(destination);
        TemplateGuard.Check(template);
        PlannedExtension[] plan = Plan(template, context, entropy, out GreaseDraw grease);
        List<RenderedExtension> rendered = Render(template, context, entropy, grease, plan, out int paddingAt);
        int padding = PaddingLength(template, context, rendered);
        if (padding >= 0)
        {
            rendered.Insert(paddingAt < 0 ? rendered.Count : paddingAt, new RenderedExtension(ExtensionType.Padding, new byte[padding]));
        }

        if (context.SessionTicket is not null)
        {
            rendered.Add(new RenderedExtension(ExtensionType.PreSharedKey, PskBody(context)));
        }

        byte[] handshake = WriteHandshake(template, entropy, grease, rendered);
        if (template.SplitAcrossRecords && handshake.Length >= 2)
        {
            int mid = handshake.Length / 2;
            WriteRecord(handshake.AsSpan(0, mid), template.RecordVersion, destination);
            WriteRecord(handshake.AsSpan(mid), template.RecordVersion, destination);
        }
        else
        {
            WriteRecord(handshake, template.RecordVersion, destination);
        }
    }

    private static PlannedExtension[] Plan(ClientHelloTemplate template, ClientHelloContext context, IEntropySource entropy, out GreaseDraw grease)
    {
        if (context.HelloRetryState is not null)
        {
            return Restore(template, context.HelloRetryState, out grease);
        }

        grease = DrawGrease(template, entropy);
        ExtensionSpec[] specs = Order(template, entropy);
        var plan = new PlannedExtension[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            plan[i] = new PlannedExtension(specs[i], ResolveType(specs[i], grease));
        }

        context.HelloRetryState = Save(grease, plan);
        return plan;
    }

    private static GreaseDraw DrawGrease(ClientHelloTemplate template, IEntropySource entropy)
    {
        ushort cipher = Uses(template.Ciphers, static code => code.Grease) ? EntropyDraw.GreaseValue(entropy) : (ushort)0;
        ushort extFirst = 0;
        ushort extLast = 0;
        int greaseExtensions = 0;
        foreach (ExtensionSpec extension in template.Extensions)
        {
            if (extension.Kind is ExtensionKind.GreaseEmpty or ExtensionKind.GreaseOneByte)
            {
                greaseExtensions++;
            }
        }

        if (greaseExtensions > 0)
        {
            extFirst = EntropyDraw.GreaseValue(entropy);
        }

        if (greaseExtensions > 1)
        {
            int draws = 0;
            do
            {
                extLast = EntropyDraw.GreaseValue(entropy);
                if (++draws > 32)
                {
                    throw new InvalidOperationException("The entropy source did not produce two different GREASE values.");
                }
            }
            while (template.ExtensionGreaseValuesDiffer && extLast == extFirst);
        }

        bool groupGrease = Uses(template.Groups, static code => code.Grease) || UsesKeyShareGrease(template);
        ushort group = groupGrease ? EntropyDraw.GreaseValue(entropy) : (ushort)0;
        ushort version = Uses(template.Versions, static code => code.Grease) ? EntropyDraw.GreaseValue(entropy) : (ushort)0;
        ushort signature = Uses(template.SignatureAlgorithms, static code => code.Grease) ? EntropyDraw.GreaseValue(entropy) : (ushort)0;
        return new GreaseDraw
        {
            Cipher = cipher,
            ExtensionFirst = extFirst,
            ExtensionLast = extLast,
            Group = group,
            Version = version,
            Signature = signature,
        };
    }

    private static ExtensionSpec[] Order(ClientHelloTemplate template, IEntropySource entropy)
    {
        var specs = (ExtensionSpec[])template.Extensions.Clone();
        if (template.Order == ExtensionOrderPolicy.Fixed)
        {
            return specs;
        }

        if (template.Order == ExtensionOrderPolicy.PermuteExceptFixed)
        {
            var moving = new List<ExtensionSpec>();
            foreach (ExtensionSpec spec in specs)
            {
                if (!spec.Fixed)
                {
                    moving.Add(spec);
                }
            }

            ExtensionSpec[] shuffled = moving.ToArray();
            EntropyDraw.Shuffle(shuffled, entropy);
            int next = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                if (!specs[i].Fixed)
                {
                    specs[i] = shuffled[next++];
                }
            }

            return specs;
        }

        var seen = new HashSet<int>();
        foreach (ExtensionSpec spec in specs)
        {
            if (spec.PermuteGroup >= 0)
            {
                seen.Add(spec.PermuteGroup);
            }
        }

        foreach (int group in seen)
        {
            var indexes = new List<int>();
            for (int i = 0; i < specs.Length; i++)
            {
                if (specs[i].PermuteGroup == group)
                {
                    indexes.Add(i);
                }
            }

            var members = new ExtensionSpec[indexes.Count];
            for (int n = 0; n < indexes.Count; n++)
            {
                members[n] = specs[indexes[n]];
            }

            EntropyDraw.Shuffle(members, entropy);
            for (int n = 0; n < indexes.Count; n++)
            {
                specs[indexes[n]] = members[n];
            }
        }

        return specs;
    }

    private static List<RenderedExtension> Render(
        ClientHelloTemplate template,
        ClientHelloContext context,
        IEntropySource entropy,
        GreaseDraw grease,
        PlannedExtension[] plan,
        out int paddingAt)
    {
        var rendered = new List<RenderedExtension>(plan.Length);
        paddingAt = -1;
        foreach (PlannedExtension planned in plan)
        {
            if (planned.Spec.Kind == ExtensionKind.Padding)
            {
                paddingAt = rendered.Count;
                continue;
            }

            byte[]? body = Body(template, context, entropy, grease, planned);
            if (body is null)
            {
                continue;
            }

            rendered.Add(new RenderedExtension(planned.Type, body));
        }

        return rendered;
    }

    private static byte[]? Body(ClientHelloTemplate template, ClientHelloContext context, IEntropySource entropy, GreaseDraw grease, PlannedExtension planned)
    {
        return planned.Spec.Kind switch
        {
            ExtensionKind.Empty or ExtensionKind.GreaseEmpty => [],
            ExtensionKind.GreaseOneByte => EntropyDraw.Bytes(entropy, 1),
            ExtensionKind.Constant => planned.Spec.ConstantBody ?? [],
            ExtensionKind.ServerName => ServerName(context.ServerName),
            ExtensionKind.Alpn => Alpn(context.ApplicationProtocols ?? template.ApplicationProtocols),
            ExtensionKind.NamedGroups => Codes(template.Groups, grease.Group, template.Fidelity),
            ExtensionKind.Versions => VersionList(template.Versions, grease.Version),
            ExtensionKind.SignatureAlgorithms => Codes(template.SignatureAlgorithms, grease.Signature, template.Fidelity),
            ExtensionKind.KeyShare => KeyShares(template, context, entropy, grease),
            ExtensionKind.EncryptedClientHelloGrease => Ech(template, entropy),
            ExtensionKind.Padding => [],
            _ => throw new TlsProtocolViolation("The template has an unknown extension generator."),
        };
    }

    private static byte[]? ServerName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        byte[] host = System.Text.Encoding.ASCII.GetBytes(name);
        var buffer = new byte[2 + 1 + 2 + host.Length];
        buffer[0] = (byte)((host.Length + 3) >> 8);
        buffer[1] = (byte)(host.Length + 3);
        buffer[2] = 0;
        buffer[3] = (byte)(host.Length >> 8);
        buffer[4] = (byte)host.Length;
        host.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    private static byte[] Alpn(IReadOnlyList<string> protocols)
    {
        int payload = 0;
        foreach (string protocol in protocols)
        {
            payload += 1 + System.Text.Encoding.ASCII.GetByteCount(protocol);
        }

        var buffer = new byte[2 + payload];
        buffer[0] = (byte)(payload >> 8);
        buffer[1] = (byte)payload;
        int offset = 2;
        foreach (string protocol in protocols)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(protocol);
            buffer[offset++] = (byte)bytes.Length;
            bytes.CopyTo(buffer.AsSpan(offset));
            offset += bytes.Length;
        }

        return buffer;
    }

    private static byte[] Codes(AdvertisedCode[] codes, ushort grease, TlsFingerprintFidelity fidelity)
    {
        int count = 0;
        foreach (AdvertisedCode code in codes)
        {
            if (Include(code, fidelity))
            {
                count++;
            }
        }

        var buffer = new byte[2 + (count * 2)];
        buffer[0] = (byte)((count * 2) >> 8);
        buffer[1] = (byte)(count * 2);
        int offset = 2;
        foreach (AdvertisedCode code in codes)
        {
            if (!Include(code, fidelity))
            {
                continue;
            }

            ushort value = code.Grease ? grease : code.Value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }

        return buffer;
    }

    private static byte[] VersionList(AdvertisedCode[] versions, ushort grease)
    {
        var buffer = new byte[1 + (versions.Length * 2)];
        buffer[0] = (byte)(versions.Length * 2);
        int offset = 1;
        foreach (AdvertisedCode version in versions)
        {
            ushort value = version.Grease ? grease : version.Value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }

        return buffer;
    }

    private static byte[] KeyShares(ClientHelloTemplate template, ClientHelloContext context, IEntropySource entropy, GreaseDraw grease)
    {
        var entries = new List<byte[]>();
        int payload = 0;
        foreach (KeyShareSlot slot in template.KeyShares)
        {
            if (slot.OmitWhenReduced && template.Fidelity == TlsFingerprintFidelity.ReducedNoPostQuantum)
            {
                continue;
            }

            if (template.Fidelity == TlsFingerprintFidelity.ReducedFipsPolicy &&
                !slot.Grease &&
                !TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, slot.Group))
            {
                continue;
            }

            byte[] key;
            if (slot.Grease)
            {
                key = EntropyDraw.Bytes(entropy, slot.Length);
            }
            else
            {
                key = RequiredShare(context, slot);
            }

            ushort group = slot.Grease ? (template.KeyShareGreaseMatchesGroup ? grease.Group : grease.ExtensionFirst) : slot.Group;
            var entry = new byte[4 + key.Length];
            entry[0] = (byte)(group >> 8);
            entry[1] = (byte)group;
            entry[2] = (byte)(key.Length >> 8);
            entry[3] = (byte)key.Length;
            key.CopyTo(entry.AsSpan(4));
            entries.Add(entry);
            payload += entry.Length;
        }

        var buffer = new byte[2 + payload];
        buffer[0] = (byte)(payload >> 8);
        buffer[1] = (byte)payload;
        int offset = 2;
        foreach (byte[] entry in entries)
        {
            entry.CopyTo(buffer.AsSpan(offset));
            offset += entry.Length;
        }

        return buffer;
    }

    private static byte[] RequiredShare(ClientHelloContext context, KeyShareSlot slot)
    {
        if (context.KeyShares is null || !context.KeyShares.TryGetValue(slot.Group, out byte[]? share) || share.Length != slot.Length)
        {
            throw new InvalidOperationException($"The ClientHello is missing a {slot.Length}-byte key share for group 0x{slot.Group:x4}.");
        }

        return share;
    }

    private static byte[] Ech(ClientHelloTemplate template, IEntropySource entropy)
    {
        if (template.EchPayloadLengths is null || template.EchPayloadLengths.Length == 0)
        {
            throw new TlsProtocolViolation("The template advertises encrypted client hello without a payload length.");
        }

        int payloadLength = template.EchPayloadLengths[EntropyDraw.Below(entropy, template.EchPayloadLengths.Length)];
        byte[] enc = EntropyDraw.Bytes(entropy, 32);
        byte[] payload = EntropyDraw.Bytes(entropy, payloadLength);
        byte config = EntropyDraw.Byte(entropy);
        var buffer = new byte[1 + 4 + 1 + 2 + enc.Length + 2 + payload.Length];
        buffer[0] = 0;
        buffer[1] = 0;
        buffer[2] = 1;
        buffer[3] = 0;
        buffer[4] = 1;
        buffer[5] = config;
        buffer[6] = 0;
        buffer[7] = 32;
        enc.CopyTo(buffer.AsSpan(8));
        int payloadAt = 8 + enc.Length;
        buffer[payloadAt] = (byte)(payload.Length >> 8);
        buffer[payloadAt + 1] = (byte)payload.Length;
        payload.CopyTo(buffer.AsSpan(payloadAt + 2));
        return buffer;
    }

    private static byte[] PskBody(ClientHelloContext context)
    {
        byte[] ticket = context.SessionTicket ?? throw new InvalidOperationException("A resumed hello needs a session ticket.");
        byte[] binder = context.PskBinder ?? throw new InvalidOperationException("A resumed hello needs a PSK binder.");
        if (binder.Length is < 32 or > 255)
        {
            throw new InvalidOperationException("A PSK binder is 32 to 255 bytes.");
        }

        int identity = 2 + ticket.Length + 4;
        var buffer = new byte[2 + identity + 2 + 1 + binder.Length];
        int offset = 0;
        offset = WriteU16(buffer, offset, identity);
        offset = WriteU16(buffer, offset, ticket.Length);
        ticket.CopyTo(buffer.AsSpan(offset));
        offset += ticket.Length;
        offset += 4;
        offset = WriteU16(buffer, offset, 1 + binder.Length);
        buffer[offset++] = (byte)binder.Length;
        binder.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static int PaddingLength(ClientHelloTemplate template, ClientHelloContext context, List<RenderedExtension> rendered)
    {
        if (template.Padding == PaddingPolicy.None)
        {
            return -1;
        }

        int extra = context.SessionTicket is null ? 0 : 4 + PskBody(context).Length;
        int record = 5 + HandshakeLength(template, rendered) + extra;
        bool withinBand = template.Padding != PaddingPolicy.PadWithinBand || (record >= template.PaddingBandMin && record <= template.PaddingBandMax);
        if (!withinBand || record >= template.PaddingTarget)
        {
            return -1;
        }

        int deficit = template.PaddingTarget - record;
        return deficit > 4 ? deficit - 4 : 0;
    }

    private static byte[] WriteHandshake(ClientHelloTemplate template, IEntropySource entropy, GreaseDraw grease, List<RenderedExtension> rendered)
    {
        var buffer = new byte[HandshakeLength(template, rendered)];
        TlsWriter writer = TlsWriter.Over(buffer);
        if (!writer.TryWriteUInt8((byte)HandshakeType.ClientHello) || !writer.TryReserveLength(3, out int lengthAt))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is short.");
        }

        if (!writer.TryWriteUInt16(template.LegacyVersion) || !writer.TryWrite(EntropyDraw.Bytes(entropy, 32)))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is short.");
        }

        if (!writer.TryWriteVector8(EntropyDraw.Bytes(entropy, template.SessionIdLength)))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is short.");
        }

        int cipherCount = IncludedCount(template.Ciphers, template.Fidelity);
        var ciphers = new byte[cipherCount * 2];
        int cipherAt = 0;
        foreach (AdvertisedCode code in template.Ciphers)
        {
            if (!Include(code, template.Fidelity))
            {
                continue;
            }

            ushort value = code.Grease ? grease.Cipher : code.Value;
            ciphers[cipherAt++] = (byte)(value >> 8);
            ciphers[cipherAt++] = (byte)value;
        }

        if (!writer.TryWriteVector16(ciphers) || !writer.TryWriteVector8([0]))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is short.");
        }

        if (!writer.TryReserveLength(2, out int extensionsAt))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is short.");
        }

        foreach (RenderedExtension extension in rendered)
        {
            if (!writer.TryWriteUInt16(extension.Type) || !writer.TryWriteVector16(extension.Body))
            {
                throw new TlsProtocolViolation("The ClientHello buffer is short.");
            }
        }

        if (!writer.TryPatchLength(extensionsAt, 2) || !writer.TryPatchLength(lengthAt, 3) || writer.Written != buffer.Length)
        {
            throw new TlsProtocolViolation("The ClientHello length does not match the bytes written.");
        }

        return buffer;
    }

    private static int HandshakeLength(ClientHelloTemplate template, List<RenderedExtension> rendered)
    {
        int extensions = 0;
        foreach (RenderedExtension extension in rendered)
        {
            extensions += 4 + extension.Body.Length;
        }

        return 4 + 2 + 32 + 1 + template.SessionIdLength + 2 + (IncludedCount(template.Ciphers, template.Fidelity) * 2) + 2 + 2 + extensions;
    }

    private static void WriteRecord(ReadOnlySpan<byte> handshake, ushort version, IBufferWriter<byte> destination)
    {
        Span<byte> header = destination.GetSpan(5 + handshake.Length);
        header[0] = 22;
        header[1] = (byte)(version >> 8);
        header[2] = (byte)version;
        header[3] = (byte)(handshake.Length >> 8);
        header[4] = (byte)handshake.Length;
        handshake.CopyTo(header[5..]);
        destination.Advance(5 + handshake.Length);
    }

    private static ushort ResolveType(ExtensionSpec spec, GreaseDraw grease)
    {
        if (spec.Kind == ExtensionKind.GreaseEmpty)
        {
            return grease.ExtensionFirst;
        }

        if (spec.Kind == ExtensionKind.GreaseOneByte)
        {
            return grease.ExtensionLast == 0 ? grease.ExtensionFirst : grease.ExtensionLast;
        }

        return spec.Type;
    }

    private static byte[] Save(GreaseDraw grease, PlannedExtension[] plan)
    {
        var buffer = new byte[15 + (plan.Length * 2)];
        buffer[0] = 1;
        WriteU16(buffer, 1, grease.Cipher);
        WriteU16(buffer, 3, grease.ExtensionFirst);
        WriteU16(buffer, 5, grease.ExtensionLast);
        WriteU16(buffer, 7, grease.Group);
        WriteU16(buffer, 9, grease.Version);
        WriteU16(buffer, 11, grease.Signature);
        WriteU16(buffer, 13, plan.Length);
        for (int i = 0; i < plan.Length; i++)
        {
            WriteU16(buffer, 15 + (i * 2), plan[i].Type);
        }

        return buffer;
    }

    private static PlannedExtension[] Restore(ClientHelloTemplate template, byte[] state, out GreaseDraw grease)
    {
        if (state.Length < 15 || state[0] != 1)
        {
            throw new ArgumentException("The HelloRetryRequest state is not a ClientHello plan.", nameof(state));
        }

        grease = new GreaseDraw
        {
            Cipher = ReadU16(state, 1),
            ExtensionFirst = ReadU16(state, 3),
            ExtensionLast = ReadU16(state, 5),
            Group = ReadU16(state, 7),
            Version = ReadU16(state, 9),
            Signature = ReadU16(state, 11),
        };
        int count = ReadU16(state, 13);
        if (state.Length != 15 + (count * 2))
        {
            throw new ArgumentException("The HelloRetryRequest state is truncated.", nameof(state));
        }

        var remaining = new List<ExtensionSpec>(template.Extensions);
        var plan = new PlannedExtension[count];
        for (int i = 0; i < count; i++)
        {
            ushort type = ReadU16(state, 15 + (i * 2));
            ExtensionSpec? match = null;
            foreach (ExtensionSpec spec in remaining)
            {
                bool greaseExtension = spec.Kind is ExtensionKind.GreaseEmpty or ExtensionKind.GreaseOneByte;
                if (greaseExtension == Grease.IsGrease(type) && (greaseExtension || spec.Type == type))
                {
                    match = spec;
                    break;
                }
            }

            if (match is null)
            {
                throw new ArgumentException("The HelloRetryRequest state does not match this profile.", nameof(state));
            }

            remaining.Remove(match);
            plan[i] = new PlannedExtension(match, type);
        }

        return plan;
    }

    private static int IncludedCount(AdvertisedCode[] codes, TlsFingerprintFidelity fidelity)
    {
        int count = 0;
        foreach (AdvertisedCode code in codes)
        {
            if (Include(code, fidelity))
            {
                count++;
            }
        }

        return count;
    }

    private static bool Include(AdvertisedCode code, TlsFingerprintFidelity fidelity)
    {
        if (fidelity == TlsFingerprintFidelity.ReducedFipsPolicy)
        {
            return code.Grease || TlsAlgorithmSet.IsApprovedOffer(code.Value);
        }

        return fidelity != TlsFingerprintFidelity.ReducedNoPostQuantum || code.Value != 0x11EC;
    }

    private static bool Uses(AdvertisedCode[] codes, Func<AdvertisedCode, bool> predicate)
    {
        foreach (AdvertisedCode code in codes)
        {
            if (predicate(code))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesKeyShareGrease(ClientHelloTemplate template)
    {
        foreach (KeyShareSlot slot in template.KeyShares)
        {
            if (slot.Grease)
            {
                return true;
            }
        }

        return false;
    }

    private static int WriteU16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
        return offset + 2;
    }

    private static ushort ReadU16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private readonly struct PlannedExtension
    {
        internal PlannedExtension(ExtensionSpec spec, ushort type)
        {
            Spec = spec;
            Type = type;
        }

        internal ExtensionSpec Spec { get; }

        internal ushort Type { get; }
    }

    private readonly struct RenderedExtension
    {
        internal RenderedExtension(ushort type, byte[] body)
        {
            Type = type;
            Body = body;
        }

        internal ushort Type { get; }

        internal byte[] Body { get; }
    }
}

internal static class EntropyDraw
{
    internal static byte Byte(IEntropySource entropy)
    {
        Span<byte> one = stackalloc byte[1];
        entropy.Fill(one);
        return one[0];
    }

    internal static byte[] Bytes(IEntropySource entropy, int count)
    {
        var buffer = new byte[count];
        if (count > 0)
        {
            entropy.Fill(buffer);
        }

        return buffer;
    }

    internal static ushort GreaseValue(IEntropySource entropy)
    {
        return Grease.Values[Byte(entropy) & 15];
    }

    internal static int Below(IEntropySource entropy, int exclusiveMax)
    {
        if (exclusiveMax <= 1)
        {
            return 0;
        }

        int limit = 256 - (256 % exclusiveMax);
        byte value;
        do
        {
            value = Byte(entropy);
        }
        while (value >= limit);

        return value % exclusiveMax;
    }

    internal static void Shuffle<T>(T[] items, IEntropySource entropy)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = Below(entropy, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
