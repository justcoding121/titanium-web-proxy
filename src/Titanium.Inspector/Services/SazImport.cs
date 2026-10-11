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

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex) when (IsEncrypted(ex))
        {
            return new SazImportResult(SazImportStatus.Encrypted, []);
        }
        catch (InvalidDataException)
        {
            return new SazImportResult(SazImportStatus.NotAnArchive, []);
        }

        using (zip)
        {
            var groups = new SortedDictionary<int, Parts>();
            long total = 0;
            try
            {
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    var match = EntryName().Match(name);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var kind = match.Groups[2].Value.ToLowerInvariant();
                    var ext = match.Groups[3].Value.ToLowerInvariant();
                    if (kind == "m" && ext != "xml")
                    {
                        continue;
                    }

                    if (kind != "m" && ext != "txt")
                    {
                        continue;
                    }

                    if (!groups.TryGetValue(index, out var parts))
                    {
                        if (groups.Count >= cap.MaxSessions)
                        {
                            return new SazImportResult(SazImportStatus.TooLarge, []);
                        }

                        parts = new Parts();
                        groups[index] = parts;
                    }

                    var bytes = ReadCapped(entry, cap.MaxEntryBytes, ref total, cap.MaxTotalBytes);
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
            }
            catch (SazEncryptedException)
            {
                return new SazImportResult(SazImportStatus.Encrypted, []);
            }
            catch (SazTooLargeException)
            {
                return new SazImportResult(SazImportStatus.TooLarge, []);
            }
            catch (InvalidDataException ex) when (IsEncrypted(ex))
            {
                return new SazImportResult(SazImportStatus.Encrypted, []);
            }
            catch (InvalidDataException)
            {
                return new SazImportResult(SazImportStatus.NotAnArchive, []);
            }

            if (groups.Count == 0)
            {
                return new SazImportResult(SazImportStatus.Empty, []);
            }

            var sessions = new List<SessionSnapshot>(groups.Count);
            var id = startId < 1 ? 1 : startId;
            foreach (var pair in groups)
            {
                if (pair.Value.Metadata is { Length: > 0 } meta && !MetadataIsSafe(meta))
                {
                    return new SazImportResult(SazImportStatus.UnsafeMetadata, []);
                }

                var session = Build(pair.Value, id);
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
    }

    private static bool HasEncryptionFlag(Stream stream)
    {
        var start = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[30];
            while (true)
            {
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
                    return true;
                }

                var compSize = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
                var nameLen = header[26] | (header[27] << 8);
                var extraLen = header[28] | (header[29] << 8);
                var skip = nameLen + extraLen + compSize;
                if (skip < 0 || stream.Position + skip > stream.Length)
                {
                    return false;
                }

                stream.Position += skip;
            }
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
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var sepLen = 4;
        if (split < 0)
        {
            split = text.IndexOf("\n\n", StringComparison.Ordinal);
            sepLen = 2;
        }

        var head = split < 0 ? text : text[..split];
        var bodyText = split < 0 ? "" : text[(split + sepLen)..];
        var lines = head.Split('\n');
        if (lines.Length == 0)
        {
            return null;
        }

        var start = lines[0].TrimEnd('\r');
        string method = "GET";
        string target = "";
        int? status = null;
        if (isRequest)
        {
            var bits = start.Split(' ');
            if (bits.Length >= 2)
            {
                method = bits[0];
                target = bits[1];
            }
        }
        else
        {
            var bits = start.Split(' ');
            if (bits.Length >= 2 && int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                status = code;
            }
        }

        var headers = new StringBuilder();
        string? host = null;
        var chunked = false;
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

        if (isRequest && !target.Contains("://", StringComparison.Ordinal) && host is not null &&
            !method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = "https://";
            target = scheme + host + (target.StartsWith('/') ? target : "/" + target);
        }

        byte[]? body = null;
        string? storedText = null;
        if (bodyText.Length > 0)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
            if (chunked)
            {
                bodyBytes = Dechunk(bodyBytes) ?? bodyBytes;
            }

            body = bodyBytes;
            storedText = Encoding.UTF8.GetString(bodyBytes);
        }

        return new RawMessage(method, target, host, headers.ToString(), status, body, storedText);
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

    private sealed class SazTooLargeException : Exception;

    private sealed class SazEncryptedException : Exception;
}
