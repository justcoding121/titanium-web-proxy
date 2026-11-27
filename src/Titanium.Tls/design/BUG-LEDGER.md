One row per bug found by the Part K campaigns. A row stays until the fix commit and the regression test both exist.

| Id | Date | Technique | Symptom | Root cause | Severity | Fix | Regression |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BH1-1 | 2026-11-27 | Flipped application record | The exception named a ciphertext byte as the alert description | `ClosedAlert` read the last byte of the record it had just written | major | this change | `ChunkingChaosTests.CampaignSelfTestCatchesAFlippedRecord` |

## What has run

`SeededSampleAgreesWithOpenSsl` draws a seeded sample (`TITANIUM_FUZZ_SEED`, default 8446; `TITANIUM_FUZZ_HANDSHAKES`, default 500). Managed-client draws stay inside what that client can complete: AES-128-GCM, X25519 or P-256, and an ECDSA P-256 certificate. Managed-server draws cover the cipher, group, certificate, ALPN, client authentication, and HelloRetryRequest choices. Each draw must carry 32 bytes both ways. The nightly budget of 10,000 was not run.

`ChunkingChaosTests` reads and writes through one-byte and three-byte fragments, including a 16,384-byte record and an empty write. `RandomInputsDoNotThrow` feeds 200 buffers from seed 8446 to `TlsFuzzTarget` and requires that they do not throw. Those buffers are not compared with OpenSSL.

Not run yet: a byte-for-byte OpenSSL oracle for arbitrary handshake mutations, boundary cases beyond the full record, three-machine timing, a mutation score, the 30-minute soak, and the public-host sweep.
