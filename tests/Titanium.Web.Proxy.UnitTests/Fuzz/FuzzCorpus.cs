using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Titanium.Web.Proxy.UnitTests.Fuzz;

/// <summary>
///     One seed of the adversarial corpus under <c>tests/fuzz/corpus</c>: the raw bytes to feed a parser plus
///     the directives (<c># expect: ...</c>, <c># assert-header: ...</c>) that say what the parser must do.
/// </summary>
internal sealed class FuzzCase
{
    public required string Name { get; init; }

    public required byte[] Bytes { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> Directives { get; init; }

    public string? Expect => Single("expect");

    public string? Single(string key)
    {
        return Directives.Where(d => d.Key == key).Select(d => d.Value).FirstOrDefault();
    }

    public IEnumerable<string> All(string key)
    {
        return Directives.Where(d => d.Key == key).Select(d => d.Value);
    }

    public override string ToString() => Name;
}

/// <summary>
///     Loads corpus files. <c>*.case</c> files hold an escaped wire message: every non-comment line is one
///     segment, segments are concatenated without a separator, and the escapes <c>\r \n \t \0 \\ \xNN</c>
///     plus <c>%REPEAT(text,count)%</c> (nestable, no commas in <c>text</c>) are expanded. Keeping the files
///     as escaped text means no editor or <c>core.autocrlf</c> setting can silently change a CRLF/LF
///     distinction that a case exists to test. <c>*.hex</c> files hold whitespace separated hex bytes.
/// </summary>
internal static partial class FuzzCorpus
{
    [GeneratedRegex(@"%REPEAT\(([^%]*?),(\d+)\)%")]
    private static partial Regex RepeatPattern();

    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "fuzz", "corpus");

    public static IReadOnlyList<FuzzCase> Load(string kind, string extension)
    {
        var directory = Path.Combine(RootDirectory, kind);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"fuzz corpus directory not found: {directory}");

        return Directory.GetFiles(directory, "*" + extension)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Parse(p, extension))
            .ToList();
    }

    private static FuzzCase Parse(string path, string extension)
    {
        var directives = new List<KeyValuePair<string, string>>();
        var payload = new StringBuilder();

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                var body = line[1..].Trim();
                var colon = body.IndexOf(':');
                if (colon > 0)
                    directives.Add(new KeyValuePair<string, string>(body[..colon].Trim(), body[(colon + 1)..].Trim()));
                continue;
            }

            // Escaped payload lines are segments of one message; hex lines need a separator between them.
            payload.Append(line);
            if (extension == ".hex") payload.Append(' ');
        }

        var bytes = extension == ".hex" ? DecodeHex(payload.ToString()) : DecodeEscaped(payload.ToString());
        return new FuzzCase
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Bytes = bytes,
            Directives = directives
        };
    }

    internal static byte[] DecodeHex(string text)
    {
        var bytes = new List<byte>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return bytes.ToArray();
    }

    internal static byte[] DecodeEscaped(string text)
    {
        string previous;
        do
        {
            previous = text;
            text = RepeatPattern().Replace(text,
                m => string.Concat(Enumerable.Repeat(m.Groups[1].Value, int.Parse(m.Groups[2].Value,
                    CultureInfo.InvariantCulture))));
        } while (previous != text);

        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\')
            {
                bytes.Add((byte)c);
                continue;
            }

            if (++i >= text.Length) throw new FormatException("dangling backslash in corpus payload");
            switch (text[i])
            {
                case 'r': bytes.Add((byte)'\r'); break;
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case '0': bytes.Add(0); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'x':
                    bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    throw new FormatException($"unknown escape \\{text[i]} in corpus payload");
            }
        }

        return bytes.ToArray();
    }
}
