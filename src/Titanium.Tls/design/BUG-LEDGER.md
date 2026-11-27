One row per bug found by the Part K campaigns. A row stays until the fix commit and the regression test both exist.

| Id | Date | Technique | Symptom | Root cause | Severity | Fix | Regression |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BH1-1 | 2026-11-27 | Flipped application record | The exception named a ciphertext byte as the alert description | `ClosedAlert` read the last byte of the record it had just written | major | `91d5a39e` | `ChunkingChaosTests.CampaignSelfTestCatchesAFlippedRecord` |
| BH1-2 | 2026-11-27 | OpenSSL hello oracle | A zero-length application record before the handshake stayed open | The empty-record cap ran before the state check, so the first 32 were ignored | minor | this change | `EngineTests.EmptyApplicationRecordBeforeTheHandshakeIsRejected` |

## What has run

`SeededSampleAgreesWithOpenSsl` draws a seeded sample (`TITANIUM_FUZZ_SEED`, default 8446; `TITANIUM_FUZZ_HANDSHAKES`, default 500). Managed-client draws stay inside what that client can complete: AES-128-GCM, X25519 or P-256, and an ECDSA P-256 certificate. Managed-server draws cover the cipher, group, certificate, ALPN, client authentication, and HelloRetryRequest choices. Each draw must carry 32 bytes both ways. The nightly budget of 10,000 was not run.

`ChunkingChaosTests` reads and writes through one-byte and three-byte fragments, including a 16,384-byte record and an empty write. `RandomInputsDoNotThrow` feeds 200 buffers from seed 8446 to `TlsFuzzTarget` and requires that they do not throw. Those buffers are not compared with OpenSSL.

`MutatedClientHelloAgreesWithOpenSsl` compares this server with OpenSSL 3.5.9 `s_server` on an intact ClientHello, a truncated hello, a zero-length application record, 33 of those records, a handshake type of 11, a supported_versions list that contains only TLS 1.2, a duplicated first extension, and an extension length of 0x7FFF. Both sides accept, both reject with the same alert, or both keep the connection open when the record is incomplete. `BoundaryLimitTests` parses certificate lists of 0, 1, and 100 entries. `SequenceNearOverflowRejectsTheNextRecord` rejects the record after `ulong.MaxValue - 1`.

`ScalarMultTimingDoesNotSeparateFixedFromRandom` and the four allocation gates ran on this PC, linuxmini, and macmini. The numbers are in `PROGRESS.md`. Welch's t was 0.31, 0.20, and 0.86.

Not run yet: a mutation score, the 30-minute soak, the nightly 10,000, and the public-host sweep.
