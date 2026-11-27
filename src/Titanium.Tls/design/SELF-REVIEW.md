Self-review checklists. Each item cites evidence. A consult happens only when an item cannot be answered from evidence.

## C1. Design, before further handshake code

| Item | Evidence |
| --- | --- |
| Sans-IO shape | `DESIGN.md` "Ownership". `TlsHkdf`, `TlsKeySchedule`, `TlsRecordProtector`, and `X25519` are synchronous and take spans. No `Task` return in `src/Titanium.Tls`. |
| State ownership | The key schedule owns three `SecretBuffer`s and zeroes a stage when it is consumed (`TlsKeySchedule`). The transcript owns its buffer and zeroes it on dispose (`TranscriptStore`). The record protector owns the AEAD instance and zeroes the IV on dispose. |
| Threading | No lock and no background work in the library today. `DESIGN.md` states the engine is single-threaded and `TlsStream` will use separate read and write locks. |
| Part O performance | One-block Expand-Label allocates 0 bytes in Release (`HkdfTests.ExpandLabelAllocatesNothingForASingleBlock`). S2 records the BCL floor in `SPIKES.md`. The transcript still rehashes; that is an open item, not an accepted design. |
| P11 names | `DESIGN.md` "Names". No public type exists yet besides the assembly marker, so there is nothing to collide. The guard test lands with the harness. |
| P8 fingerprints | `DESIGN.md` "Fingerprints", backed by the Chrome 158 captures and `SPIKES.md`. The engine does not exist yet, so the "no Chrome outside the profile" rule has no violating code. |

Closed after C1: the transcript hash is incremental, `TryUnprotect` returns `RecordResult`, and stage secrets are rented from `SecretBufferPool`. The X25519 60 µs target is missed; the measurements are in `PROGRESS.md`.

## C3. Key schedule, records, and key exchange

| Item | Evidence |
| --- | --- |
| Secret lifetimes | `TlsKeySchedule` zeroes the early secret in `AdvanceToHandshake` and the handshake secret in `AdvanceToMaster`. The master secret is wiped once both application secrets, the exporter master, and the resumption master exist (`CompleteZeroesTheMasterAfterTheApplicationSecrets`). `DisposeZeroesEveryStageSecret` covers the pool. HKDF salt and the all-zero IKM are cleared before `HKDF.Extract`. The record IV is wiped on dispose (`DisposeZeroesTheIv`). Hybrid decapsulation keys are pinned and wiped in `HybridPrivateShare.Dispose`. |
| Nonce | `WriteNonce` copies the 12-byte IV and XORs the sequence into the last 8 bytes (RFC 8446 section 5.3). `UnprotectsRfc8448ServerHandshakeFlight` and `UnprotectsRfc8448ClientFinished` decrypt records built with that nonce. A second decrypt of the same bytes is `bad_record_mac` (`RejectsTamperReplayOverflowAndEmptyInner`). |
| Limits | Inner plaintext is content, the content-type byte, and padding, and it must fit in 2^14 (RFC 8446 section 5.4). `Protect` rejects a 2^14-byte content, accepts 2^14−1, and rejects `padding: int.MaxValue` without wrapping. `TryUnprotect` rejects a ciphertext whose inner length would exceed 2^14 before it decrypts. Ciphertext length above 2^14+256 is `record_overflow` on both the protected and plaintext readers. |
| Stage guards | `EnsureStage` rejects an advance from the wrong stage (`OutOfOrderAdvanceThrows`). RFC 8448 sections 3, 4, and 5 match (`KeyScheduleTests`). SHA-384 is checked against an independent HMAC. |
| Hybrid order | Client share is the 1184-byte encapsulation key then 32-byte X25519. Server share is the 1088-byte ciphertext then X25519. The secret is ML-KEM then X25519. `ClientAndServerDeriveTheSameSecret` and `OpenSslClientHelloUsesTheSameShareOrder`. |
| Encoding checks | Wrong share lengths and a damaged encapsulation key fail closed (`WrongLengthsAndADamagedEncapsulationKeyAreRejected`). A damaged ciphertext still decapsulates and the secret changes. NIST shares reject a bad prefix and an off-curve point (`KeyExchangeTests`). X25519 rejects an all-zero shared secret. |
| CCS window | `TryAcceptChangeCipherSpec` accepts one plaintext record of type 20, version 0x0301 or 0x0303, and body `0x01`, then rejects a second (`PlaintextHeaderRejectsOverflowAndAcceptsOneChangeCipherSpec`). Which handshake messages may be separated by that record is the engine's rule; the engine does not exist yet. |
| Malformed peer input | A bad tag, a truncated record, an unknown outer content type, and an all-zero inner plaintext return `RecordResult` instead of throwing (`TryUnprotect`). Header versions other than 0x0301 and 0x0303 are rejected. Key-share length checks return false. |

No consult. Two majors were fixed in this review: the inner-plaintext limit was one byte too loose, and the HKDF zero salt was an uncleared `stackalloc`. The handshake-message-must-not-span-a-key-change rule stays with the engine.

## C4. Fingerprint derivation against the captures

| Item | Evidence |
| --- | --- |
| What was measured | Fifty TCP ClientHellos, Chrome 158.0.8127.89, fresh profile, `https://localhost/`. Record `0x0301`, legacy `0x0303`, session id 32, compression `{0}`, one record, no padding, no `pre_shared_key`. Cipher, group, version, and signature tails are constant once GREASE is removed. The extension set is constant and the middle 17 move. GREASE ends differ; the first body is empty and the last body is one byte. The GREASE group equals the GREASE key share. Other GREASE slots matched an independent draw (1–5 of 50). ECH is outer type 0, KDF `0x0001`, AEAD `0x0001`, enc length 32, payload length one of 144, 176, 208, 240. Key shares are GREASE:1, `0x11EC`:1216, `0x001D`:32. ALPS is `17613` with body `0003 02 6832`. Extension `0xCA34` is one 186-byte constant. `ConstantExtensionBodiesMatchTheCapture` compares those bodies to `hello-001.bin`. `GeneratedChromeHellosMatchTheCapturedShape` checks JA4, JA3 field sets, and that each middle extension appears in each middle slot across 1,000 hellos. |
| What was assumed | No HelloRetryRequest and no resumed hello are in the capture. Replaying the extension order is `HelloRetryRequestKeepsExtensionOrder`. `pre_shared_key` last is the RFC 8446 rule, checked by `ResumedHelloPutsPreSharedKeyLast`. `ReducedNoPostQuantum` drops `0x11EC` (`ReducedFidelityOmitsTheHybridShare`); every capture on this PC is the full shape. Payload lengths are the 50 observed values, with their counts, not a model of the inner layout. The shuffle is Fisher-Yates. The capture histogram (about 1–9 hits per cell) is consistent with that and is not a proof of uniformity. The profile does not mint real key shares. The capture showed the hybrid X25519 half never equalled the standalone share (0 of 50); that check belongs to the engine when it builds shares. QUIC Initials are not in this set. |
| Drift | `InstalledChromeMatchesThePinnedInvariants` is `Explicit`. It launches Chrome only when `TITANIUM_CHROME_DRIFT=1`, captures eight loopback ClientHellos, and compares them to `hello-001.bin` through `ChromeHelloInvariants`. A normal suite does not start a browser. Run: `$env:TITANIUM_CHROME_DRIFT='1'; dotnet test tests/Titanium.Tls.Tests --filter TestCategory=Explicit`. Eight fresh hellos matched on this PC. A shape change fails the test. A version number change with the same shape does not. Refresh with `tools/managed-stack/Capture-ClientHello.ps1`, then update the profile and `MANIFEST.md`. |
| Risks | JA4 ignores extension bodies, GREASE, and group-list contents, so the body comparison is the check that matters. `0xCA34` stays opaque. TLS 1.2 ciphers and version `0x0303` are advertised and not implemented; the abort is the client state machine, which is not built yet. `ClientHelloTemplate` stays internal until a second browser exists. |

No consult. The profile constants matched the capture, including `0xCA34`. The gap this review closed is that JA4 alone does not pin extension bodies.

## C6. Stream concurrency, KeyUpdate, shutdown, and cancellation

| Item | Evidence |
| --- | --- |
| One owner per direction | `TlsStream` takes `_readLock` for reads and `_writeLock` for writes. Authentication and dispose take the read lock and then the write lock. A reader that must send KeyUpdate waits for the write lock while it already holds the read lock. The writer never takes the read lock, so that order cannot deadlock. |
| KeyUpdate | RFC 8446 section 4.6.3. The read path rotates only the read secret (`OnKeyUpdate`). `update_requested` sets `KeyUpdateResponseNeeded`. The writer emits that response, or its own update, under the old write key and then rotates (`ProtectKeyUpdate`). `KeyUpdateKeepsTheFollowingRecordReadable` and `KeyUpdateSurvivesConcurrentTraffic` cover both directions. An empty body is `illegal_parameter` (`EmptyKeyUpdateIsIllegal`). |
| Shutdown | `ShutdownAsync` holds the write lock, sends warning `close_notify`, and later writes throw the `SslStream` message (`WriteAfterShutdownThrows`). The reader does not send KeyUpdate after that flag is set. A received `close_notify` makes `Read` return 0. |
| Cancellation | A cancel during an inner read or after a record is protected marks the stream unusable. `CancelledReadThrows` checks the cancel and the following `IOException`. Cancel while waiting for a lock, before I/O, does not. |
| Dispose | Dispose closes the inner stream first so a blocked read unblocks, then takes both locks and zeroes the engine. `ReadAfterDisposeThrows`. |
| Parity | `TlsStreamTests` covers the S1 rows, a 10 MiB hash compare both ways, ALPN, the cipher, and `SslStream` in both directions. Differences are in `PARITY-DIFFS.md`. |

No consult. Two gaps were closed: a KeyUpdate response was dropped when the reader could not take the write lock immediately, and a cancel during I/O left the record layer usable. Still deferred, and not a second design: the locks are taken on every call rather than only when the direction is busy, the receive buffer is a fixed full record rather than a pooled 4 KiB buffer that grows, and a steady-state 16 KiB read or write is not yet allocation-free. Those belong to the performance gates.
