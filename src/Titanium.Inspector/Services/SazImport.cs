using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Titanium.Inspector.Services;

public enum SazImportStatus
{
    Ok,
    Empty,
    Encrypted,
    TooLarge,
    UnsafeMetadata,
    NotAnArchive,
}

public readonly record struct SazLimits(int MaxSessions, long MaxEntryBytes, long MaxTotalBytes)
{
    public static SazLimits Default { get; } = new(5_000, 32L * 1024 * 1024, 256L * 1024 * 1024);
}

public readonly record struct SazImportResult(SazImportStatus Status, List<SessionSnapshot> Sessions);

/// <summary>
/// Reads a Fiddler SAZ archive in memory. Entry names must be <c>raw/N_c.txt</c>,
/// <c>raw/N_s.txt</c>, or <c>raw/N_m.xml</c>. Nothing is written to disk.
/// </summary>
public static partial class SazImport
{
    [GeneratedRegex(@"^raw/(\d+)_(c|s|m)\.(txt|xml)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EntryName();

    public static SazImportResult Read(Stream stream, long startId, SazLimits? limits = null)
    {
        var cap = limits ?? SazLimits.Default;
        if (stream.CanSeek && HasEncryptionFlag(stream))
        {
            return new SazImportResult(SazImportStatus.Encrypted, []);
        }

        if (!TryOpenZipArchive(stream, out var zip, out var openFailure))
        {
            return new SazImportResult(openFailure, []);
        }

        using (zip)
        {
            if (!TryCollectParts(zip, cap, out var groups, out var readFailure))
            {
                return new SazImportResult(readFailure, []);
            }

            if (groups.Count == 0)
            {
                return new SazImportResult(SazImportStatus.Empty, []);
            }

            return BuildSessionsFromParts(groups, startId);
        }
    }

    private static bool TryOpenZipArchive(Stream stream, out ZipArchive zip, out SazImportStatus failure)
    {
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            failure = SazImportStatus.Ok;
            return true;
        }
        catch (InvalidDataException ex) when (IsEncrypted(ex))
        {
            zip = null!;
            failure = SazImportStatus.Encrypted;
            return false;
        }
        catch (InvalidDataException)
        {
            zip = null!;
            failure = SazImportStatus.NotAnArchive;
            return false;
        }
    }

    private static bool TryCollectParts(
        ZipArchive zip,
        SazLimits cap,
        out SortedDictionary<int, Parts> groups,
        out SazImportStatus failure)
    {
        groups = new SortedDictionary<int, Parts>();
        long total = 0;
        try
        {
            foreach (var entry in zip.Entries)
            {
                if (!TryParseEntryName(entry.FullName, out var index, out var kind))
                {
                    continue;
                }

                if (!groups.TryGetValue(index, out var parts))
                {
                    if (groups.Count >= cap.MaxSessions)
                    {
                        failure = SazImportStatus.TooLarge;
                        groups = new SortedDictionary<int, Parts>();
                        return false;
                    }

                    parts = new Parts();
                    groups[index] = parts;
                }

                var bytes = ReadCapped(entry, cap.MaxEntryBytes, ref total, cap.MaxTotalBytes);
                AssignPartBytes(parts, kind, bytes);
            }
        }
        catch (SazEncryptedException)
        {
            failure = SazImportStatus.Encrypted;
            groups = new SortedDictionary<int, Parts>();
            return false;
        }
        catch (SazTooLargeException)
        {
            failure = SazImportStatus.TooLarge;
            groups = new SortedDictionary<int, Parts>();
            return false;
        }
        catch (InvalidDataException ex) when (IsEncrypted(ex))
        {
            failure = SazImportStatus.Encrypted;
            groups = new SortedDictionary<int, Parts>();
            return false;
        }
        catch (InvalidDataException)
        {
            failure = SazImportStatus.NotAnArchive;
            groups = new SortedDictionary<int, Parts>();
            return false;
        }

        failure = SazImportStatus.Ok;
        return true;
    }

    private static bool TryParseEntryName(string fullName, out int index, out string kind)
    {
        index = 0;
        kind = "";
        var name = fullName.Replace('\\', '/');
        var match = EntryName().Match(name);
        if (!match.Success)
        {
            return false;
        }

        index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        kind = match.Groups[2].Value.ToLowerInvariant();
        var ext = match.Groups[3].Value.ToLowerInvariant();
        if (kind == "m" && ext != "xml")
        {
            return false;
        }

        if (kind != "m" && ext != "txt")
        {
            return false;
        }

        return true;
    }

    private static void AssignPartBytes(Parts parts, string kind, byte[] bytes)
    {
        if (kind == "c")
        {
            parts.Request = bytes;
        }
        else if (kind == "s")
        {
            parts.Response = bytes;
        }
        else
        {
            parts.Metadata = bytes;
        }
    }

    private static SazImportResult BuildSessionsFromParts(SortedDictionary<int, Parts> groups, long startId)
    {
        var sessions = new List<SessionSnapshot>(groups.Count);
        var id = startId < 1 ? 1 : startId;
            foreach (var parts in groups.Values)
            {
                if (parts.Metadata is { Length: > 0 } meta && !MetadataIsSafe(meta))
                {
                    return new SazImportResult(SazImportStatus.UnsafeMetadata, []);
                }

                var session = Build(parts, id);
            if (session is not null)
            {
                sessions.Add(session);
                id++;
            }
        }

        return sessions.Count == 0
            ? new SazImportResult(SazImportStatus.Empty, [])
            : new SazImportResult(SazImportStatus.Ok, sessions);
    }

    private static bool HasEncryptionFlag(Stream stream)
    {
        var start = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[30];
            while (TryReadLocalHeader(stream, header, out var encrypted, out var skip))
            {
                if (encrypted)
                {
                    return true;
                }

                if (skip < 0 || stream.Position + skip > stream.Length)
                {
                    return false;
                }

                stream.Position += skip;
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static bool TryReadLocalHeader(Stream stream, Span<byte> header, out bool encrypted, out int skip)
    {
        encrypted = false;
        skip = 0;
        var read = stream.Read(header);
        if (read < 4)
        {
            return false;
        }

        if (header[0] != 0x50 || header[1] != 0x4B)
        {
            return false;
        }

        if (header[2] == 0x01 && header[3] == 0x02)
        {
            return false;
        }

        if (header[2] != 0x03 || header[3] != 0x04 || read < 30)
        {
            return false;
        }

        var flags = header[6] | (header[7] << 8);
        if ((flags & 1) != 0)
        {
            encrypted = true;
            skip = 0;
            return true;
        }

        var compSize = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
        var nameLen = header[26] | (header[27] << 8);
        var extraLen = header[28] | (header[29] << 8);
        skip = nameLen + extraLen + compSize;
        return true;
    }

    private static bool IsEncrypted(InvalidDataException ex) =>
        ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadCapped(ZipArchiveEntry entry, long entryCap, ref long total, long totalCap)
    {
        Stream input;
        try
        {
            input = entry.Open();
        }
        catch (InvalidDataException ex) when (IsEncrypted(ex))
        {
            throw new SazEncryptedException();
        }

        using (input)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int n;
                try
                {
                    n = input.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException ex) when (IsEncrypted(ex))
                {
                    throw new SazEncryptedException();
                }

                if (n == 0)
                {
                    return ms.ToArray();
                }

                if (ms.Length + n > entryCap || total + n > totalCap)
                {
                    throw new SazTooLargeException();
                }

                ms.Write(buffer, 0, n);
                total += n;
            }
        }
    }

    private static bool MetadataIsSafe(byte[] xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            while (reader.Read())
            {
                // Consume the document; malformed XML throws from Read().
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static SessionSnapshot? Build(Parts parts, long id)
    {
        var request = ParseMessage(parts.Request, isRequest: true);
        var response = ParseMessage(parts.Response, isRequest: false);
        if (request is null && response is null)
        {
            return null;
        }

        var method = request?.Method ?? "GET";
        var session = new SessionSnapshot
        {
            Id = id,
            ConnectionId = 0,
            Method = method,
            Url = request?.Target ?? "",
            Host = request?.Host,
            RequestHeadersText = request?.Headers,
            RequestBodyBytes = request?.Body,
            RequestBodyText = request?.BodyText,
            StatusCode = response?.StatusCode,
            ResponseHeadersText = response?.Headers,
            ResponseBodyBytes = response?.Body,
            ResponseBodyText = response?.BodyText,
            IsTunnel = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase),
            TimingEstimated = true,
            Protocol = "HTTP/1.1",
        };
        if (session.RequestBodyBytes is { Length: > 0 })
        {
            session.RequestBodyCapture = BodyCaptureState.Complete;
        }

        if (session.ResponseBodyBytes is { Length: > 0 })
        {
            session.ResponseBodyCapture = BodyCaptureState.Complete;
        }

        return session;
    }

    private static RawMessage? ParseMessage(byte[]? raw, bool isRequest)
    {
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(raw);
        SplitHeadAndBody(text, out var head, out var bodyText);
        var lines = head.Split('\n');
        if (lines.Length == 0)
        {
            return null;
        }

        ParseStartLine(lines[0].TrimEnd('\r'), isRequest, out var method, out var target, out var status);
        ParseHeaderBlock(lines, headers: out var headers, host: out var host, chunked: out var chunked);

        if (isRequest && !target.Contains("://", StringComparison.Ordinal) && host is not null &&
            !method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = "https://";
            target = scheme + host + (target.StartsWith('/') ? target : "/" + target);
        }

        BuildBodyFields(bodyText, chunked, out var body, out var storedText);
        return new RawMessage(method, target, host, headers.ToString(), status, body, storedText);
    }

    private static void SplitHeadAndBody(string text, out string head, out string bodyText)
    {
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var sepLen = 4;
        if (split < 0)
        {
            split = text.IndexOf("\n\n", StringComparison.Ordinal);
            sepLen = 2;
        }

        head = split < 0 ? text : text[..split];
        bodyText = split < 0 ? "" : text[(split + sepLen)..];
    }

    private static void ParseStartLine(string start, bool isRequest, out string method, out string target, out int? status)
    {
        method = "GET";
        target = "";
        status = null;
        var bits = start.Split(' ');
        if (isRequest)
        {
            if (bits.Length >= 2)
            {
                method = bits[0];
                target = bits[1];
            }

            return;
        }

        if (bits.Length >= 2 && int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            status = code;
        }
    }

    private static void ParseHeaderBlock(
        string[] lines,
        out StringBuilder headers,
        out string? host,
        out bool chunked)
    {
        headers = new StringBuilder();
        host = null;
        chunked = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            headers.Append(line).Append("\r\n");
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                     value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }
    }

    private static void BuildBodyFields(string bodyText, bool chunked, out byte[]? body, out string? storedText)
    {
        body = null;
        storedText = null;
        if (bodyText.Length == 0)
        {
            return;
        }

        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        if (chunked)
        {
            bodyBytes = Dechunk(bodyBytes) ?? bodyBytes;
        }

        body = bodyBytes;
        storedText = Encoding.UTF8.GetString(bodyBytes);
    }

    internal static byte[]? Dechunk(byte[] body)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < body.Length)
        {
            var lineEnd = IndexOfCrlf(body, i);
            if (lineEnd < 0)
            {
                return null;
            }

            var sizeText = Encoding.ASCII.GetString(body, i, lineEnd - i);
            var semi = sizeText.IndexOf(';');
            if (semi >= 0)
            {
                sizeText = sizeText[..semi];
            }

            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                size < 0)
            {
                return null;
            }

            i = lineEnd + 2;
            if (size == 0)
            {
                return output.ToArray();
            }

            if (i + size > body.Length)
            {
                return null;
            }

            output.Write(body, i, size);
            i += size;
            if (i + 1 < body.Length && body[i] == '\r' && body[i + 1] == '\n')
            {
                i += 2;
            }
        }

        return output.ToArray();
    }

    private static int IndexOfCrlf(byte[] body, int start)
    {
        for (var i = start; i + 1 < body.Length; i++)
        {
            if (body[i] == '\r' && body[i + 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Parts
    {
        public byte[]? Request { get; set; }
        public byte[]? Response { get; set; }
        public byte[]? Metadata { get; set; }
    }

    private sealed record RawMessage(
        string Method,
        string Target,
        string? Host,
        string Headers,
        int? StatusCode,
        byte[]? Body,
        string? BodyText);

    public sealed class SazTooLargeException : Exception;

    public sealed class SazEncryptedException : Exception;
}
