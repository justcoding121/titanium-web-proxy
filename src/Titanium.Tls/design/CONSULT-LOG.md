Earlier reviews are recorded in `SELF-REVIEW.md`. This log is the Phase 1 gate consult.

## C7

One read-only whole-library review of `src/Titanium.Tls`, asked for blockers around the state machine, parsing, secret handling, timing, DoS caps, and resumption.

One blocker: a required client certificate was not enforced, and resumption skipped the request. Seven majors: the server ignored `RemoteCertificateValidationCallback` and did not expose the client certificate; a peer-triggered alert was encrypted on the reader thread; the client accepted any server ALPN; a large ticket or cookie could truncate the next ClientHello and stay cached; repeating one cache key grew the eviction queue; a certificate list was uncapped before chain building; every engine allocated the maximum handshake buffers up front.

Applied as described in `SELF-REVIEW.md`. The certificate-list cap is 100, not 10 to 16, because `CertificateListsOfZeroOneAndOneHundredParse` already treats 100 as legal. The idle-memory gate was not re-measured. No second consult.
