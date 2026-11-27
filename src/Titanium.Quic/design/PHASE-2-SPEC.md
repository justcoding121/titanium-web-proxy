Phase 2 builds QUIC v1 in `Titanium.Quic` on top of the Phase 1 TLS engine. The proxy still uses `System.Net.Quic`. This spec is the kickoff. It is the task list. Later milestones follow it unless a written reason changes a locked decision.

## Decisions this kickoff locks

These follow Part B and Part O. They are not new product choices.

- QUIC version 1 only. The version field is a number so version 2 can be added later. This phase does not negotiate it.
- The core is sans-IO. `QuicConnectionCore` takes a time and a datagram and returns datagrams and a timeout. `TimeProvider` supplies time. No socket call lives in the core.
- Crypto stays on the caller. The core does not return a `Task` or a `ValueTask`. The UDP driver is the only place that waits.
- Pipes and channels stay banned. Nothing in this phase measures a pipe as faster, so the kickoff does not open that exception.
- The only new managed cipher is the ChaCha20 block used for header protection. AES header protection uses `Aes.EncryptEcb`. Record protection uses the BCL AEAD.
- One shared timer in the driver. The core only reports `NextTimeout`. There is no `Timer` per connection.
- Receive processing and application writes run the core inline under a short per-connection lock. There is no per-connection thread and no queue between the socket and the core.
- Buffers are rented when a datagram arrives and returned when the connection is idle. The 4 KiB idle target is a gate to measure, not a claim.
- 0-RTT packets are parsed. This phase does not send or accept early application data. That is Phase 3.
- A Chrome transport-parameter profile is data from a capture. No capture is in the tree yet, so the engine sends a documented layout and the profile task waits. Bytes are not invented.
- `System.Net.Quic` is not used as a listener on this PC. Interop where we are the client of a platform listener runs on `macmini` and `linuxmini`.
- FIPS uses the same fail-closed rule as `TlsStream`.
- NewReno ships first. CUBIC is a later task and becomes the default only after it wins the simulator and the loopback and delay benchmarks.
- UDP offload is a separate project, after the portable socket transport is correct and measured. An offload path that is not faster is not kept.
- Public types stay internal until the public-API task. `PublicAPI.Unshipped.txt` stays empty until then.

## Core shape

`QuicConnectionCore.HandleDatagram(DateTimeOffset now, ReadOnlySpan<byte> datagram, in PathInfo path)` decrypts, runs frames, and may arm a transmit. `PollTransmit(now, Span<byte> destination)` writes one datagram and reports how many bytes were used. `NextTimeout()` and `HandleTimeout(now)` drive loss, PTO, idle, and path probing. The TLS handshake bytes move in CRYPTO frames through `TlsEngine`. There is no TLS record layer on this path. `quic_transport_parameters` is extension 57, which the TLS engine already accepts. Keys are installed per encryption level.

The driver types are `ManagedQuicListener`, `ManagedQuicConnection`, and `ManagedQuicStream`. They are BCL-shaped and appear in the public-API task, not before it.

## Tasks

### T2.1 Variable-length integers

RFC 9000 section 16. Encode with the shortest form. Decode any legal form, including a longer form than the shortest. A value above 2^62−1 is rejected. A truncated buffer returns false rather than throwing.

Exit: round trips at 0, 63, 64, 16383, 16384, 2^30−1, 2^30, and 2^62−1, plus a non-minimal encoding and a short buffer.

### T2.2 Long and short headers

RFC 9000 section 17.1 and 17.2. Long headers: Initial, 0-RTT, Handshake, Retry. Short headers. Version Negotiation is a first-byte form with version 0. Coalesced packets in one datagram stop at the first short header. Packet-number length comes from the low bits. Connection IDs are bounded by the length byte. A datagram that is not a whole header is rejected, not thrown.

Exit: fixtures for each long type, a short header, a coalesced Initial plus Handshake, a Version Negotiation packet, and a truncated header.

### T2.3 Packet numbers

RFC 9000 Appendix A. Encode the trailing bytes. Decode with the expected next number. A number that does not reconstruct is a protocol error.

Exit: the appendix examples, plus a reconstruction across a 2-byte boundary.

### T2.4 Initial secrets and header protection

RFC 9001 section 5.2 and section 5.4, and RFC 8439 for the ChaCha20 block. The v1 salt is the published salt. AES mask is one `Aes.EncryptEcb` of the 16-byte sample. ChaCha20 uses a managed block whose counter is taken from the sample, because the BCL AEAD starts its counter at 1. One cached `Aes` per header-protection key.

Exit: RFC 9001 Appendix A sample packets, client and server, for the Initial secret, key, IV, header-protection key, and the protected samples. The ChaCha20 block matches the RFC 8439 block vectors. Header protection is applied and removed on an Appendix A packet.

### T2.5 Retry integrity

RFC 9001 section 5.8. The retry integrity tag is checked before a Retry is accepted. A bad tag is discarded. The pseudo-packet layout matches the RFC.

Exit: the RFC 9001 retry sample, a flipped tag, and a Retry whose original destination connection ID does not match.

### T2.6 Frames

RFC 9000 section 12.4 and section 19, plus RFC 9221 DATAGRAM. Parse and write every frame this phase accepts: PADDING, PING, ACK with ECN counts, RESET_STREAM, STOP_SENDING, CRYPTO, NEW_TOKEN, STREAM, MAX_DATA, MAX_STREAM_DATA, MAX_STREAMS, DATA_BLOCKED, STREAM_DATA_BLOCKED, STREAMS_BLOCKED, NEW_CONNECTION_ID, RETIRE_CONNECTION_ID, PATH_CHALLENGE, PATH_RESPONSE, CONNECTION_CLOSE, HANDSHAKE_DONE, DATAGRAM. ACK of ranges is capped. STREAM offsets and lengths are capped. A frame that is illegal in that packet type is a connection error. An unknown frame type is a connection error.

Exit: one round trip per frame, an ACK with ECN, a STREAM with and without length, a DATAGRAM, an ACK in an Initial (illegal), and a CRYPTO in a 1-RTT packet (legal).

### T2.7 Transport parameters

RFC 9000 section 18. Codec and the required checks: `initial_source_connection_id`, `original_destination_connection_id`, `retry_source_connection_id`, `stateless_reset_token`, `preferred_address` parse, `grease_quic_bit`, `max_datagram_frame_size`. A missing required parameter is a transport-parameter error. A duplicate is an error. Grease parameters are ignored.

Exit: a client and a server parameter list round trip, each missing-required case, a duplicate, and a grease id.

### T2.8 Connection core

The four methods above, plus path identity. A datagram for an unknown destination connection ID does not allocate a connection. Anti-amplification is three times the bytes received before the address is validated. The core does not send more than that.

Exit: a scripted datagram that produces no transmit over the limit, and a timeout that fires from `HandleTimeout` without a socket.

### T2.9 Handshake over CRYPTO

Client and server complete a QUIC handshake by feeding CRYPTO bytes to `TlsEngine` and installing packet keys from the TLS secrets at each level. The transport parameters ride in extension 57. Handshake bytes above the TLS cap are a crypto error. The TLS record protector is not used.

Exit: two in-process cores reach the application level and exchange one STREAM byte. A bad Finished closes the connection. A transport-parameter mismatch closes it.

### T2.10 Loss recovery

RFC 9002. RTT samples, time threshold, packet threshold, PTO, persistent congestion. NewReno is the controller behind `ICongestionController`. Pacing uses the shared timeout, not a per-packet timer. An ACK of an unsent packet is `PROTOCOL_VIOLATION`.

Exit: the simulator drops one packet and it is retransmitted; a reordered packet is not declared lost; persistent congestion shrinks the window; an ACK of an unsent packet closes.

### T2.11 Identity and address validation

Connection ID issuance and retirement, stateless reset tokens compared with `FixedTimeEquals`, Retry and NEW_TOKEN, path challenge and response. A retry token that fails integrity is ignored. Retirement of the last active connection ID is an error.

Exit: a Retry round trip, a NEW_TOKEN used on the next connection, a stateless reset that does not decrypt, and a path challenge that validates a new path.

### T2.12 Streams and flow control

Stream state machines, MAX_DATA, MAX_STREAM_DATA, MAX_STREAMS. A flow-control violation is `FLOW_CONTROL_ERROR`. CRYPTO and STREAM reassembly have a byte cap. A gap larger than the cap is an error. Reset and stop-sending are delivered once.

Exit: a stream that blocks at the credit and resumes, a peer that exceeds the credit, and a reassembly buffer that rejects a huge gap.

### T2.13 Idle, keep-alive, and migration

Idle timeout from the transport parameters. A keep-alive is a PING or a PADDING packet under the loss rules, not a private timer. Migration follows a validated path. An unvalidated path does not raise the congestion window.

Exit: an idle connection times out, a keep-alive prevents it, and a migration before validation does not move the send limit.

### T2.14 Path MTU discovery

RFC 8899 and RFC 9000 section 14.3. Start at 1200 bytes. Probe upward with PADDING. Lower the ceiling when the transport reports a tunnel maximum. `Socket.DontFragment` is set where the socket supports it. A probe that is lost shrinks the ceiling. The core never sends a UDP payload above the current ceiling, and never below 1200 once the path is validated, except for the PMTU probes themselves.

Exit: a simulator MTU of 1400 settles between 1200 and 1400, and a black hole returns the ceiling to 1200.

### T2.15 UDP transport

`IUdpPacketTransport`: local endpoint, maximum datagram size, receive, send, optional batch, optional DF and ECN. `UdpSocketTransport` uses `ReceiveFromAsync(Memory<byte>, SocketFlags, SocketAddress)`, pooled addresses, and several concurrent receives. `SO_REUSEPORT` is used on Linux when the option exists. `Inject` accepts a datagram and an outbound callback for relays and tunnels. The virtual tuple is what amplification and path validation key on. GSO, GRO, `sendmmsg`, `recvmmsg`, and a portable PMTU socket API are documented gaps, not this task.

Exit: two loopback sockets exchange one datagram on an ephemeral port. Inject delivers one datagram and the callback sees the reply. No listener is bound to a fixed port.

### T2.16 Public API

`ManagedQuicConnection`, `ManagedQuicListener`, `ManagedQuicStream`, and the options and callbacks that the BCL types cannot take because they seal the platform connection. `QuicException` and `QuicError` are the BCL types. Names match the BCL member they stand in for. XML docs on every public member. `PublicAPI.Unshipped.txt` lists exactly those members.

Exit: a loopback connect, one bidirectional stream, a hash-compared payload, close, and the architecture test still seeing no proxy reference and no P/Invoke.

### T2.17 Simulator, interop, and negatives

An in-process network with loss, reordering, duplication, delay, and an MTU. qlog is a debug sink, off by default. Interop with `System.Net.Quic` in both roles runs where a platform listener is allowed. Negative rows go in `TRACEABILITY.md`. Fuzz seeds cover packet and frame parsers and must not throw.

Exit: the simulator cases from T2.10 through T2.14, one platform interop on a machine that may listen, and a corpus that stays inside the documented errors.

### T2.18 Benchmarks

BenchmarkDotNet against `System.Net.Quic`: handshake rate, many small streams, bulk loopback. Report median and the confidence interval. Gates: handshake at least 0.90, small-flow at least 0.90, bulk on the portable path recorded, bulk with offload on Linux at least 0.80 once that project exists. A miss is recorded. The gate is not lowered. Numbers go on `wiki/Performance-Local-Lab.md`. Laptop numbers do not go on `wiki/Performance.md`.

### Later tasks, not the first milestones

CUBIC behind `ICongestionController`, default only if it beats NewReno. `Titanium.Quic.UdpOffload` after T2.15 is benchmarked, and only for a measured gain. The Chrome Initial layout when a capture exists. BH-2 at the end of the phase. A whole-library security review and a performance review at the phase gate, then a written report.

## Self-review

Checked against Part B and Part O.

| Rule | How this spec keeps it |
| --- | --- |
| B1, B15 | Version 1 only. No BBR. No QUIC v2 implementation. |
| B2, B4 | ChaCha20 block only. No `Task` in the core. No P/Invoke in this project. |
| B3, O0 | Sans-IO core, one driver timer, inline receive and send. Pipes stay banned. |
| B9, B11 | BCL exception and error types. Public surface delayed. No proxy reference. |
| B12 | Appendix A, RFC 8439, and the simulator are the oracles. The BCL is an interop peer, not the only oracle. |
| B13 | The package README already says experimental. |
| B24 | NewReno first. CUBIC is gated on a measurement. |
| O1 item 2 and 10 | Rent buffers. Short per-connection lock. Shared timeout. |
| O2 QUIC row | The numbers are gates in T2.18. None is claimed here. |
| Test rules | Loopback and ephemeral ports. No platform listener on this PC. No fixed ports from the banned list. |

No consult. Two limits are recorded so they are not forgotten: the Chrome Initial capture is still absent, and the idle-memory number will be a re-measure after connections exist.
