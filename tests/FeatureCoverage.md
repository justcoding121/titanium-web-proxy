# Feature-to-Test Traceability Matrix

Maps every public capability, HTTP/1.x/HTTP/2 protocol behavior, and bug fix covered by the HTTP/1.x and
HTTP/2 gap-closure work to the automated test(s) that exercise it. All projects target `net10.0` only (see
Phase 0). This is the audited Phase 4 version of the matrix started in Phase 0A; entries are grouped by area
and cross-reference [`wiki/Protocol-Support.md`](../wiki/Protocol-Support.md) rows where applicable.

Legend: **U** = `tests/Titanium.Web.Proxy.UnitTests`, **I** = `tests/Titanium.Web.Proxy.IntegrationTests`.

## HTTP/1.x framing and lifecycle

| Feature / behavior | Test(s) | Kind |
|---|---|---|
| Explicit HTTP proxying, request/response interception (GET/POST/PUT/PATCH/DELETE) | `InterceptionTests.Can_Intercept_*_Requests` | I |
| HTTPS decrypt-and-inspect, fake (fabricated) tunnel certs, mutual TLS | `HttpsTests.Can_Handle_Https_Request`, `Can_Handle_Https_Fake_Tunnel_Request`, `Can_Handle_Https_Mutual_Tls_Request` | I |
| Keep-alive semantics per HTTP version (`Connection`/no header, 1.0 vs 1.1) | `ResponseKeepAliveTests.Http11_*`, `Http10_*`, `Http2_NoConnectionHeader_IsKeepAlive` | U |
| Connection pooling enabled/disabled, reuse across requests | `ConnectionPoolTests.Connection_Pool_Is_Enabled_By_Default_And_Reuses_Server_Connection`, `Connection_Pool_Disabled_Does_Not_Reuse_Across_Client_Connections` | I |
| Upstream proxy connection cache key correctness (credentials, DNS-via-proxy, protocol negotiation) | `ConnectionCacheKeyTests.*` | U |
| `Expect: 100-continue` (continue, expectation-failed, not-found, handler throws) | `ExpectContinueTests.ReverseProxy_*` | I |
| Chunked trailers - read/write, malformed/oversized/forbidden fields, unit-level | `ChunkedTrailerTests` (U) - `ReadTrailingHeaders_*`, `WriteTrailingHeadersAsync_*`, `WriteRawTrailingLinesAsync_*` | U |
| Chunked trailers - end-to-end relay, multiple trailers, no-trailer body, connection reuse after drain, request trailers, `Respond()` draining an unread original body | `ChunkedTrailerTests` (I) - `Chunked_Response_Trailer_Is_Forwarded_To_Client`, `Chunked_Response_With_Multiple_Trailers_Are_All_Forwarded`, `Chunked_Response_Without_Trailers_Still_Relays_Body_And_Terminator_Correctly`, `Chunked_Request_Trailer_Is_Forwarded_To_Server`, `Chunked_Response_Trailer_Fully_Drained_Allows_Pooled_Connection_Reuse`, `BeforeResponse_Respond_With_Custom_Response_Drains_Original_Chunked_Body_Without_Throwing` | I |
| Interim 1xx responses: 103 Early Hints, repeated 1xx, unsolicited 100 discarded, 101 upgrade not looped as interim | `InterimResponseTests.*` | I |
| WebSocket / `101 Switching Protocols` upgrade regression | `InterimResponseTests.SwitchingProtocols_101_Response_Is_Relayed_Exactly_Once_Not_Looped_As_Interim` | I |
| Per-chunk body-write hooks, plain HTTP: pass-through byte-for-byte, in-place rewrite | `StreamingBodyTests.OnResponseBodyWrite_Passthrough_Is_Byte_For_Byte`, `OnResponseBodyWrite_Can_Rewrite_Body` | I |
| Per-chunk body-write hooks, TLS-decrypted (Phase 1 `ITransportCapableStream` fix): relay parity and rewrite | `StreamingBodyTests.OnResponseBodyWrite_Tls_Decrypted_Http11_Body_Relays_Correctly_And_Hook_Fires`, `OnResponseBodyWrite_Tls_Decrypted_Http11_Can_Rewrite_Body` | I |
| Large response streamed incrementally without full buffering | `StreamingBodyTests.Large_Response_Streams_Incrementally_Without_Full_Buffering` | I |
| `RespondStreaming` synthetic body, chunked and fixed-length framing | `StreamingBodyTests.RespondStreaming_Chunked_Generates_Body_Without_Contacting_Server`, `RespondStreaming_FixedLength_Writes_Raw_With_ContentLength` | I |
| `HeaderCollection` unique/non-unique storage, case-insensitivity, `Proxy-Connection` folding | `HeaderCollectionTests.*` | U |
| `RequestResponseBase` body/content-length/chunked invariants, compression, `TrailingHeaders` default/identity | `RequestResponseBaseTests.*` | U |
| Low-level `HttpStream`/`ILineStream` line and partial-write correctness (regression for TLS EOF fix) | `StreamAndCertificateRegressionTests.WriteAsync_ReadOnlyMemory_WritesOnlyRequestedBytes`, `ReadLineAsync_*` | U |
| Reverse-proxy smoke tests (all http/https combinations, tunnel-without-decryption, fixed forward endpoint) | `ReverseProxyTests.Smoke_Test_*` | I |
| Nested/chained proxy farms, upstream failover, connection-cache hang regressions | `NestedProxyTests.*` | I |
| Upstream proxy authentication (CONNECT and plain HTTP) | `UpstreamProxyAuthTests.Authenticates_*` | I |
| Windows auth (NTLM/Kerberos) token acquisition and connection-affinity reuse across auth re-requests | `WinAuthTests.*` | U |
| System/WinHTTP proxy settings resolution, merge/replace/validate rules | `SystemProxyTest.*` | U |
| Certificate generation (BC and Windows store), server-cert cache/regeneration, null-session callback safety | `CertificateManagerTests.*`, `StreamAndCertificateRegressionTests.CertificateCallbacks_NullSessionUseSafeDefaultsWithoutInvocation` | U |
| Concurrency/load: one server with many concurrent clients | `StressTests.Stress_Test_With_One_Server_And_Many_Clients` | I |

## HTTP/2

| Feature / behavior | Test(s) | Kind |
|---|---|---|
| HPACK decode of fragmented string literals; dynamic-table wraparound after capacity change | `HpackRegressionTests.Decode_FragmentedStringLiteral_EmitsCompleteHeader`, `DynamicTable_WrappedEntriesSurviveCapacityChangeInIndexOrder` | U |
| HPACK encoder dynamic-table reuse across calls vs. fresh-instance-per-call (Phase 2 fix) | `Http2HpackEncoderTests.Encoder_ReusedInstance_IndexesRepeatedHeaderIntoDynamicTable`, `Encoder_FreshInstancePerCall_NeverIndexesRepeatedHeader` | U |
| HPACK eviction under many distinct/repeated headers still decodes correctly (regression for `Encoder.Add`/`StaticTable.GetIndex` fixes) | `Http2HpackEvictionTests.*` | U |
| Repeated response/request headers round-trip correctly across multiple requests on the same connection (dynamic-table desync regression) | `Http2Tests.Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests`, `Http2_Repeated_Request_Header_Round_Trips_Correctly_Across_Multiple_Requests` | I |
| Many concurrent multiplexed streams with distinct headers do not cross-contaminate | `Http2Tests.Http2_Many_Concurrent_Streams_With_Distinct_Headers_Do_Not_Cross_Contaminate` | I |
| Response/request trailers (second HEADERS block) relayed without re-firing before-events | `Http2TrailerInterimContinuationTests.Http2_Response_Trailers_From_Origin_Are_Relayed_To_Client`, `Http2_Request_Trailers_From_RawClient_Are_Relayed_To_Origin` | I |
| Interim 1xx response relayed on its own HEADERS frame before the final response | `Http2TrailerInterimContinuationTests.Http2_Interim_1xx_Response_Is_Relayed_Before_Final_Response` | I |
| HEADERS/CONTINUATION reassembly (large header split across CONTINUATION frames, reassembled and relayed) | `Http2TrailerInterimContinuationTests.Http2_Large_Response_Header_Split_Across_Continuation_Is_Reassembled_And_Relayed` | I |
| Two-hop flow control: window accounting, blocking/unblocking on WINDOW_UPDATE, overflow detection, initial-window-size changes, cancellation, concurrent waiters | `Http2FlowControllerTests.*` (14 cases) | U |
| RST_STREAM relayed and connection remains usable for further streams | `Http2ProtocolTests.Http2_RstStream_From_Origin_Is_Relayed_And_Connection_Remains_Usable_For_Further_Streams` | I |
| GOAWAY causes local refusal of new streams above the peer's last-accepted id | `Http2ProtocolTests.Http2_GoAway_From_Origin_Causes_Local_Refusal_Of_New_Stream_Above_Last_Accepted_Id` | I |
| Oversized frame triggers connection-level `FRAME_SIZE_ERROR` via GOAWAY | `Http2ProtocolTests.Http2_Oversized_Frame_From_Client_Triggers_GoAway_With_FrameSizeError` | I |
| Malformed SETTINGS ACK (non-zero length) triggers `FRAME_SIZE_ERROR` | `Http2ProtocolTests.Http2_Settings_Ack_With_NonZero_Length_Triggers_GoAway_With_FrameSizeError` | I |
| Zero-increment WINDOW_UPDATE on a stream/connection triggers the correct `PROTOCOL_ERROR` (RST_STREAM vs GOAWAY) | `Http2ProtocolTests.Http2_WindowUpdate_Zero_Increment_On_Stream_Triggers_RstStream_With_ProtocolError`, `..._On_Connection_Triggers_GoAway_With_ProtocolError` | I |
| Synthetic responses (`Ok`/`GenericResponse`/`Redirect`/buffered `Respond`) from `BeforeRequest`: origin never contacted, client gets the synthetic response | `Http2SyntheticResponseTests.Http2_Ok_From_BeforeRequest_Answers_Client_And_Origin_Never_Sees_Request`, `Http2_GenericResponse_From_BeforeRequest_Answers_Client_With_Given_Status`, `Http2_Redirect_From_BeforeRequest_Answers_Client_With_Location_Header`, `Http2_Buffered_Respond_From_BeforeRequest_Answers_Client_Without_Body` | I |
| `BeforeResponse`-time `Respond()` replacing an already-received response (stale-reference fix; origin body suppressed) | `Http2SyntheticResponseTests.Http2_BeforeResponse_Respond_Replaces_Already_Received_Response` | I |
| Body-write hook parity over HTTP/2 (rewrite) | `StreamingBodyTests.OnResponseBodyWrite_Http2_Can_Rewrite_Body` | I |
| `RespondStreaming` synthetic body over HTTP/2 without contacting the server | `StreamingBodyTests.RespondStreaming_Http2_Generates_Body_Without_Contacting_Server` | I |
| Keep-alive semantics inherent to HTTP/2 multiplexing | `ResponseKeepAliveTests.Http2_NoConnectionHeader_IsKeepAlive` | U |

## Deferred / explicitly out of scope

These are intentionally not implemented and therefore have no positive test coverage; each is called out in
`wiki/Protocol-Support.md` and the `ProxyServer.EnableHttp2` XML doc so it is not re-discovered as "missing":

- HTTP/2 server push (`PUSH_PROMISE`) — no public API, frames not decoded/transcoded.
- Cleartext `h2c` upgrade — HTTP/2 is only ever negotiated via TLS ALPN.
- True HTTP/1.1 pipelining (multiple in-flight requests without waiting for each response).
- `Transfer-Encoding: compress|deflate|gzip` (as opposed to `Content-Encoding`, which is fully supported).

## Notes on baseline comparison

- Phase 0A captured the pre-retarget baseline on the then-current multi-targeted framework set; Phase 0B's
  atomic net10.0 retarget was verified against that baseline with no unexplained regressions (see the
  `phase0-*` commit history on `develop`).
- Every bug fixed during Phases 1–3 (chunked-trailer draining, TLS body-write-hook gating, HPACK dynamic-table
  desync, the shared-frame-header corruption bug, the stale-response-reference bug, and others) has a test
  above that fails against the pre-fix code and passes after the fix.
- No repository-wide coverage percentage is enforced; this matrix is reviewed by changed area instead, per
  the Phase 4 policy of no new protocol state-machine branch shipping untested without a documented reason.
