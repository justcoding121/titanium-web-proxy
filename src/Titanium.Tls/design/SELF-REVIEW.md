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

Open, and not a roadblock: incremental transcript, `RecordResult` instead of exceptions, secret pooling, X25519 speed. Those are specified and still to implement.
