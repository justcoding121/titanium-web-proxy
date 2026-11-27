One row per bug found by the QUIC campaign. A row stays until the fix commit and the regression test both exist.

| Id | Date | Technique | Symptom | Root cause | Severity | Fix | Regression |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BH2-1 | 2026-11-27 | Managed client, platform listener | The handshake completes. The platform peer does not accept the stream within 15 seconds. | Open. Earlier closes were a datagram that kept only the first QUIC packet, a destination connection ID the server had not issued, and a store of two connection IDs. | major | open | `AManagedClientReachesThePlatformListener` |

## What has run

The in-process network corrupts a datagram and can black-hole every datagram after the handshake. `BytesArriveOnceAndInOrder` sends 256 bytes through delay, reordering, and duplication. `OneCorruptedDatagramIsNotDeliveredAsStreamData` flips one bit of one datagram. `ABlackholeDropsThePayload` sends 32 bytes into a network that drops everything. `AFlippedHandshakeDatagramDoesNotThrow` delivers a bit-flipped Initial and the handshake still completes. An empty datagram is not queued. A DATAGRAM frame closes the connection with protocol violation 0x0a and does not become stream data. Packet and frame seeds already refuse to throw.

On linuxmini, with `TITANIUM_QUIC_PLATFORM_LISTENER=1`, a managed client completes a handshake to a platform listener. The following stream is not accepted. A platform client to the managed listener already exchanges 32 bytes on this PC. The public interop runner, h3spec, Stryker, and the 30 minute soak have not run. One dropped datagram still closes the receiver with flow-control error 0x03 once the next flight passes the 16384-byte reassembly cap. That limit is recorded and is not a new row.
