Phase 2 started from the kickoff in `PHASE-2-SPEC.md`. The proxy does not call this library.

## T2.1

Variable-length integers follow RFC 9000 section 16. The shortest encoding is what this stack writes. A longer legal encoding still decodes. A truncated buffer returns false.
