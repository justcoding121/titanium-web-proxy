One row per bug found by the Part K campaigns. A row stays until the fix commit and the regression test both exist.

| Id | Date | Technique | Symptom | Root cause | Severity | Fix | Regression |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BH1-1 | 2026-11-27 | Flipped application record | The exception named a ciphertext byte as the alert description | `ClosedAlert` read the last byte of the record it had just written | major | `91d5a39e` | `ChunkingChaosTests.CampaignSelfTestCatchesAFlippedRecord` |
| BH1-2 | 2026-11-27 | OpenSSL hello oracle | A zero-length application record before the handshake stayed open | The empty-record cap ran before the state check, so the first 32 were ignored | minor | this change | `EngineTests.EmptyApplicationRecordBeforeTheHandshakeIsRejected` |

## What has run

`SeededSampleAgreesWithOpenSsl` draws a seeded sample (`TITANIUM_FUZZ_SEED`, default 8446; `TITANIUM_FUZZ_HANDSHAKES`, default 500). The nightly budget of 10,000, seed 8446, agreed with OpenSSL 3.5.9 on every draw and took 1 hour 44 minutes. Managed-client draws stay inside what that client can complete: AES-128-GCM, X25519 or P-256, and an ECDSA P-256 certificate. Managed-server draws cover the cipher, group, certificate, ALPN, client authentication, and HelloRetryRequest choices. Each draw must carry 32 bytes both ways.

`ChunkingChaosTests` reads and writes through one-byte and three-byte fragments, including a 16,384-byte record and an empty write. `RandomInputsDoNotThrow` feeds 200 buffers from seed 8446 to `TlsFuzzTarget` and requires that they do not throw. Those buffers are not compared with OpenSSL.

`MutatedClientHelloAgreesWithOpenSsl` compares this server with OpenSSL 3.5.9 `s_server` on an intact ClientHello, a truncated hello, a zero-length application record, 33 of those records, a handshake type of 11, a supported_versions list that contains only TLS 1.2, a duplicated first extension, and an extension length of 0x7FFF. Both sides accept, both reject with the same alert, or both keep the connection open when the record is incomplete. `BoundaryLimitTests` parses certificate lists of 0, 1, and 100 entries. `SequenceNearOverflowRejectsTheNextRecord` rejects the record after `ulong.MaxValue - 1`.

`ScalarMultTimingDoesNotSeparateFixedFromRandom` and the four allocation gates ran on this PC, linuxmini, and macmini. The numbers are in `PROGRESS.md`. Welch's t was 0.31, 0.20, and 0.86.

Stryker 5.1.0, Standard, Debug, four workers, scored the thirteen handshake, codec, key schedule, and record files. The run excluded `Slow`, `TimingSideChannel`, `TlsInteropTests`, `HandshakeOracleTests`, and `ScalarMultStaysUnderTheRegressionCeiling`. It tested 2417 mutants. The score is 70.06 percent: 1880 killed, 109 timed out, 428 survived, and 422 with no coverage. Timeouts count as detected. No-coverage mutants stay in the denominator.

One hundred mutants are ignored as equivalent. Fifty-six are a byte stored from a shift: an unsigned shift matches the arithmetic shift for a non-negative value, and a left shift cast to that byte is zero while the value stays under the shift width. Thirty-three wipe a temporary after its bytes have been copied and are not read again. Eleven are a `return` after a handshake-type test whose following conditions do not match that type. The comments naming those reasons sit on the statements.

The assertions that killed the rest of the length mutants are `HandshakeCodecTests.MultiByteLengthsRoundTrip`, `HandshakeCodecTests.LengthAtTheCapIsDistinctFromOnePastIt`, `ClientHandshakeTests.HelloRetryForAnUnlistedGroupIsIllegal`, `ClientHandshakeTests.ClientHelloEncodesWideServerNameAndProtocolList`, and `ClientHandshakeTests.PreSharedKeyEncodesAWideIdentityAndAge`.

The 428 survivors stay for the phase report. They fall into four groups. An unsigned shift of a non-negative length shares its statement with a killed left shift, so that statement stays in the score. Peer parsers for a group id, a selected PSK index, and a ticket timestamp still see a zero high byte in this suite. `Dispose` and the wipes of `_psk` and `ticket.Psk` have no assertion after they run. `TlsEngine.Traffic.cs` (42.34 percent), `TlsEngine.cs` (63.69 percent), and `TlsEngine.Server.cs` (65.34 percent) are the files under 70 percent; the thirteen files together are 70.06 percent.

`SoakTests.ThirtyMinutesOfHandshakesStayBounded` ran for 30 minutes. Completed exchanges, a one-byte abort, a reset after the ClientHello, and a one-byte-per-second hello all finished without an escaped exception. In flight stayed at or under 200, which is the test's cap: the engine has no handshake limiter to enforce. `ArrayPool.Shared` does not publish a rent count, so the pool balance is not a number in this run. After a forced collection the managed heap was within 32 MiB of the baseline taken once four warm handshakes had completed.

`RealPeerTests.ManagedClientHandshakesWellKnownHosts`, with `TITANIUM_REAL_PEERS=1`, handshook five hosts on port 443 and no other port. `www.cloudflare.com`, `www.google.com`, `github.com`, and `www.wikipedia.org` completed with `TLS_AES_128_GCM_SHA256` and `http/1.1`. `www.microsoft.com` sent alert 40. OpenSSL 3.5.9 with only `ecdsa_secp256r1_sha256`, X25519, P-256, and AES-128-GCM-SHA256 gets that same alert, and a normal OpenSSL client completes TLS 1.3 there. The failure is the client's existing signature offer, recorded in `PARITY-DIFFS.md`, not a new defect.

Still to run: the three-machine gate with `TITANIUM_REQUIRE_ORACLE=1`.
