namespace Titanium.Web.Proxy.Options;

/// <summary>
///     The resource-bound policy families that support an <see cref="Options.PolicyMode" /> other than
///     <see cref="PolicyMode.Enforce" />, per the plan's rollout section. Framing, chunk parsing and
///     <c>Content-Length</c>/<c>Transfer-Encoding</c> resolution are deliberately not members of this
///     enum: they are always enforced and never consult a mode, because there is no safe
///     <see cref="PolicyMode.Observe" /> action for an ambiguous or malformed message - see
///     <see cref="ProxyPolicyModes" /> and <c>AllowAmbiguousFraming</c> for the one explicit,
///     isolated escape hatch from that rule.
/// </summary>
public enum PolicyFamily
{
    /// <summary>
    ///     Cumulative whole-body buffering limits (<c>MaxBufferedBodyBytes</c> and the
    ///     <see cref="ProxyResourceLimits.MaxEncodedBodyBytes" />/<see cref="ProxyResourceLimits.MaxDecodedBodyBytes" />
    ///     pair) enforced via <see cref="Network.Streams.BoundedWriteStream" /> across H1, H2 and H3.
    /// </summary>
    BodyBudget,

    /// <summary>
    ///     The compressed-input/decompressed-output byte budgets and the expansion-ratio ceiling
    ///     (<see cref="ProxyResourceLimits.MaxDecompressionRatio" />) applied while draining a
    ///     <c>Content-Encoding</c> chain, so a small compressed body cannot expand unboundedly in
    ///     memory before <see cref="PolicyFamily.BodyBudget" />'s own decoded-byte cap would catch it.
    ///     <para>
    ///         Today, that decoded-byte cap is exactly what protects this case in practice: the
    ///         decompression chain writes into the same <see cref="Network.Streams.BoundedWriteStream" />-
    ///         wrapped target <see cref="BodyBudget" /> already bounds, so a small compressed body that
    ///         expands enormously is caught the moment the decoded output crosses that limit, without
    ///         needing a separately computed ratio. <see cref="ProxyResourceLimits.MaxDecompressionRatio" />
    ///         and the encoded/decoded byte pair remain reserved for a future, more precise per-stream
    ///         computation; this family's mode exists now so a profile can name it, but changing it has
    ///         no additional effect while <see cref="BodyBudget" /> already covers the same paths.
    ///     </para>
    /// </summary>
    DecompressionRatio,

    /// <summary>
    ///     Header line length, header count and aggregate header-byte limits
    ///     (<see cref="ProxyResourceLimits.MaxHeaderLineBytes" />/<see cref="ProxyResourceLimits.MaxHeaderCount" />/
    ///     <see cref="ProxyResourceLimits.MaxHeaderAggregateBytes" />) intended for the request/response
    ///     header-block read.
    ///     <para>
    ///         Reserved, like <see cref="DecompressionRatio" />, for numeric enforcement not yet wired
    ///         to every header-reading call site; this family's mode exists so a profile can name it
    ///         ahead of that work landing. The client request-line/header <em>deadline</em> (a
    ///         different, already-enforced protection - see <c>ProxyServer.ClientHeaderTimeoutSeconds</c>
    ///         and <c>DeadlineRegistry</c>) is unaffected by this family's mode.
    ///     </para>
    /// </summary>
    HeaderLimits,

    /// <summary>
    ///     The global and per-endpoint admission gates that bound concurrently admitted client
    ///     connections.
    /// </summary>
    AdmissionControl,

    /// <summary>
    ///     HTTP/2 abuse budgets: the open-header-block CONTINUATION frame-count/wall-clock bound and
    ///     the peer-initiated incomplete-stream-reset budget.
    /// </summary>
    Http2AbuseBudget,

    /// <summary>
    ///     Whether HPACK header blocks on the H2↔H2 compressed-relay path
    ///     (<c>httpInterceptionEnabled = false</c>) are semantically validated per RFC 9113 §8.3.
    ///     Unlike framing (always enforced, no Observe action), header semantics can be logged
    ///     without corrupting connection state.
    ///     <para>
    ///         <see cref="PolicyMode.Disabled"/> (default on <see cref="ProxyProfile.Balanced"/>)
    ///         skips HPACK decode entirely — maximum throughput when upstream peers are trusted.
    ///     </para>
    ///     <para>
    ///         <see cref="PolicyMode.Observe"/> decodes and records semantic violations without
    ///         rejecting the stream. Does not mutate headers, so the compressed-relay
    ///         <c>MutationCount</c> fast path is unaffected when the family is Disabled.
    ///     </para>
    ///     <para>
    ///         <see cref="PolicyMode.Enforce"/> (default on <see cref="ProxyProfile.PublicFacing"/>
    ///         and <see cref="ProxyPolicyModes.AllEnforce"/>) decodes and sends
    ///         <c>GOAWAY(PROTOCOL_ERROR)</c> on violations.
    ///     </para>
    /// </summary>
    Http2RelayValidation
}
