Phase 2 started from the kickoff in `PHASE-2-SPEC.md`. The proxy does not call this library.

## T2.1

Variable-length integers follow RFC 9000 section 16. The shortest encoding is what this stack writes. A longer legal encoding still decodes. A truncated buffer returns false.

## T2.2

Long and short headers follow RFC 9000 section 17. A datagram is a sequence of packets until a short header, a Retry, or a Version Negotiation packet, each of which consumes the rest. A version 1 connection ID longer than 20 bytes is rejected. A Version Negotiation connection ID may be longer, because that packet quotes whatever the peer sent. A truncated header returns false. The header still exposes the truncated packet-number bytes. `QuicPacketNumber` recovers the full value.

## T2.3

Packet numbers follow RFC 9000 Appendix A. The encoder uses the appendix width: 0xac5c02 after an acknowledgment of 0xabe8b3 is two bytes, and 0xace8fe in that same state is three. A 16-bit field of 0x9b32 after 0xa82f30ea decodes to 0xa82f9b32. A two-byte field also reconstructs across 0x10000 in both directions. A candidate left outside the recovery window is rejected, as is a gap that does not fit in four bytes.

## T2.4

Initial secrets use the RFC 9001 salt and `TlsHkdf`. Client and server keys, IVs, and header-protection keys match Appendix A for connection ID `8394c8f03e515708`. AES header protection is one cached ECB encryption of the 16-byte sample. The ChaCha20 block matches the RFC 8439 block vector, and the Appendix A.5 short-header mask uses that block with the counter taken from the sample. Protect and remove are the same exclusive-or, with the packet-number length read after the first byte is uncovered.

## T2.5

The Retry integrity tag is AES-128-GCM over the pseudo-packet: the original destination connection ID length, that connection ID, then the Retry packet without its tag. The published key and nonce match the Expand-Label of the RFC secret. The Appendix A.4 sample verifies. A flipped tag is discarded, and so is a Retry whose original destination connection ID does not match.
