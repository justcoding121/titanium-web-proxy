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
