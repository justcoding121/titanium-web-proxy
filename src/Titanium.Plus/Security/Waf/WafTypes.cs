using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Security.Waf;

/// <summary>What the WAF does with a request that reaches the block threshold.</summary>
public enum WafMode
{
    /// <summary>No screening.</summary>
    Off = 0,

    /// <summary>Screen, log, and count. Never refuses a request.</summary>
    Detect = 1,

    /// <summary>Screen and refuse a request whose anomaly score reaches the block threshold with 403.</summary>
    Block = 2,
}

/// <summary>Severity of a rule. The default anomaly weight of each level is its numeric value.</summary>
public enum WafSeverity
{
    Notice = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>Parts of a request that a rule reads.</summary>
[Flags]
public enum WafTargets
{
    None = 0,
    Path = 1,
    Query = 2,
    Header = 4,
    Cookie = 8,
    Body = 16,
    All = Path | Query | Header | Cookie | Body,
}

/// <summary>A change applied to a value before a custom rule reads it.</summary>
public enum WafTransform
{
    /// <summary>Percent-decodes (<c>%XX</c>, <c>%uXXXX</c>) one time and turns <c>+</c> into a space.</summary>
    UrlDecode,

    /// <summary>Lower-cases with the invariant culture.</summary>
    Lowercase,

    /// <summary>Drops NUL characters.</summary>
    RemoveNulls,

    /// <summary>Decodes numeric and common named HTML entities.</summary>
    HtmlEntityDecode,
}

/// <summary>What a matching rule contributes.</summary>
public enum WafAction
{
    /// <summary>Adds the rule score (or the severity weight) to the anomaly score.</summary>
    Score,

    /// <summary>Adds the score and, in block mode, refuses the request whatever the total.</summary>
    Block,

    /// <summary>Records the match and adds nothing to the score.</summary>
    Log,
}

/// <summary>Request checks that need more than a pattern on one value.</summary>
public enum WafCheck
{
    None = 0,
    InvalidContentLength,
    DuplicateHost,
    NullByte,
    EncodingDepth,
    OversizedArg,
    ContentLengthWithTransferEncoding,
}

/// <summary>One rule that matched a request.</summary>
public readonly record struct WafMatch(string RuleId, string Category, WafSeverity Severity, WafTargets Target, string? Name, int Score);

/// <summary>Why a request body was or was not inspected.</summary>
public enum WafBodyOutcome
{
    /// <summary>The request has no body.</summary>
    None = 0,

    /// <summary>The body was parsed and its text values were screened.</summary>
    Inspected,

    /// <summary>The content type is not urlencoded, JSON, or multipart. Binary uploads land here.</summary>
    NotEligible,

    /// <summary>The body is larger than <c>waf.inspectBodyBytes</c>.</summary>
    TooLarge,

    /// <summary>The body has no Content-Length (chunked). It would have to be buffered without a bound.</summary>
    UnknownLength,

    /// <summary>The body carries a Content-Encoding. The WAF screens the bytes as sent and does not decompress.</summary>
    Encoded,

    /// <summary>Body inspection is off (<c>waf.inspectBodyBytes</c> is 0).</summary>
    Disabled,
}

/// <summary>Result of screening one request.</summary>
public readonly struct WafVerdict
{
    public static WafVerdict Clean(WafMode mode) => new(mode, 0, false, false, null, 0);

    public WafVerdict(WafMode mode, int score, bool wouldBlock, bool blocked, WafMatch[]? matches, int timeouts)
    {
        Mode = mode;
        Score = score;
        WouldBlock = wouldBlock;
        Blocked = blocked;
        Matches = matches ?? [];
        RuleTimeouts = timeouts;
    }

    public WafMode Mode { get; }

    /// <summary>Sum of the rule scores. Each rule counts once per request.</summary>
    public int Score { get; }

    /// <summary>True when the score reached the threshold or a <c>block</c> rule matched, in any mode but off.</summary>
    public bool WouldBlock { get; }

    /// <summary>True only in <see cref="WafMode.Block"/> when <see cref="WouldBlock"/> is true.</summary>
    public bool Blocked { get; }

    public IReadOnlyList<WafMatch> Matches { get; }

    /// <summary>Rule evaluations that hit the match timeout. Counted, never scored.</summary>
    public int RuleTimeouts { get; }
}

/// <summary>The parts of a request the engine reads. Strings are borrowed for the call.</summary>
public readonly struct WafRequest
{
    /// <summary>Request target as received: <c>/path?query</c> or an absolute URI.</summary>
    public string Target { get; init; }

    /// <summary>Request headers, or null when only the path and query are known.</summary>
    public HeaderCollection? Headers { get; init; }

    /// <summary>The body, already limited to <c>waf.inspectBodyBytes</c>. Empty when it was not read.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The Content-Type used to choose a body parser.</summary>
    public string? ContentType { get; init; }
}
