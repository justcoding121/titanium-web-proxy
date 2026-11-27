Phase 2 started from the kickoff in `PHASE-2-SPEC.md`. The proxy does not call this library.

## T2.1

Variable-length integers follow RFC 9000 section 16. The shortest encoding is what this stack writes. A longer legal encoding still decodes. A truncated buffer returns false.

## T2.2

Long and short headers follow RFC 9000 section 17. A datagram is a sequence of packets until a short header, a Retry, or a Version Negotiation packet, each of which consumes the rest. A version 1 connection ID longer than 20 bytes is rejected. A Version Negotiation connection ID may be longer, because that packet quotes whatever the peer sent. A truncated header returns false. Packet numbers are still the raw bytes; reconstruction is T2.3.
