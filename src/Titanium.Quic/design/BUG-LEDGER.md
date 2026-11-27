One row per bug found by the QUIC campaign. A row stays until the fix commit and the regression test both exist.

| Id | Date | Technique | Symptom | Root cause | Severity | Fix | Regression |
| --- | --- | --- | --- | --- | --- | --- | --- |

## What has run

The in-process network now corrupts a datagram and can black-hole every datagram after the handshake. `BytesArriveOnceAndInOrder` sends 256 bytes through delay, reordering, and duplication. `OneCorruptedDatagramIsNotDeliveredAsStreamData` flips one bit of one datagram. `ABlackholeDropsThePayload` sends 32 bytes into a network that drops everything. Packet and frame seeds already refuse to throw.

These have not run: a differential against System.Net.Quic in both directions beyond the existing listener smoke, the public interop runner, h3spec, Stryker, a 30 minute soak, and the frame cases this stack does not implement (connection migration as a completed path change, NAT rebinding, key update, Retry as a required path, version negotiation, stateless reset as a peer, and DATAGRAM frames). One dropped datagram still closes the receiver with flow-control error 0x03 once the next flight passes the 16384-byte reassembly cap. That limit is recorded and is not a new row.
