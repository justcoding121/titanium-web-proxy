Phase 2 started from the kickoff in `PHASE-2-SPEC.md`. The proxy does not call this library.

## T2.1

Variable-length integers follow RFC 9000 section 16. The shortest encoding is what this stack writes. A longer legal encoding still decodes. A truncated buffer returns false.

## T2.2

Long and short headers follow RFC 9000 section 17. A datagram is a sequence of packets until a short header, a Retry, or a Version Negotiation packet, each of which consumes the rest. A version 1 connection ID longer than 20 bytes is rejected. A Version Negotiation connection ID may be longer, because that packet quotes whatever the peer sent. A truncated header returns false. The header still exposes the truncated packet-number bytes. `QuicPacketNumber` recovers the full value.

## T2.3

Packet numbers follow RFC 9000 Appendix A. The encoder uses the appendix width: 0xac5c02 after an acknowledgment of 0xabe8b3 is two bytes, and 0xace8fe in that same state is three. A 16-bit field of 0x9b32 after 0xa82f30ea decodes to 0xa82f9b32. A two-byte field also reconstructs across 0x10000 in both directions. A candidate left outside the recovery window is rejected, as is a gap that does not fit in four bytes.
