Rows are `RFC | section | requirement | test id | status`. A test id is `Class.Method`. These are the negative and simulator rows for the QUIC stack.

| RFC | Section | Requirement | Test id | Status |
| --- | --- | --- | --- | --- |
| RFC 9000 | 12.4 | A truncated header or varint does not throw | QuicFuzzTests.PacketAndFrameSeedsDoNotThrow | negative |
| RFC 9000 | 12.4 | An unknown frame type is a frame encoding error | QuicFrameTests.UnknownTypeAndNonMinimalTypeAndEmptyPayloadAreErrors | negative |
| RFC 9000 | 17.2 | A truncated or oversized header is rejected | QuicPacketHeaderTests.TruncatedAndOversizedHeadersAreRejected | negative |
| RFC 9000 | 13.2.3 | An acknowledgment of a packet that was never sent closes the connection | QuicLossTests.AnAckOfAnUnsentPacketCloses | negative |
| RFC 9000 | 4.6 | Data past the advertised limit closes with a flow-control error | QuicFlowTests.APeerThatExceedsTheCreditClosesTheConnection | negative |
| RFC 9000 | 7.5 | A reassembly gap above the cap is rejected | QuicFlowTests.AHugeReassemblyGapIsRejected | negative |
| RFC 9001 | 5.8 | A Retry with a bad integrity tag is ignored | QuicIdentityTests.ARetryWithABadTagIsIgnored | negative |
| RFC 9000 | 10.1 | An idle connection times out | QuicIdleTests.AnIdleConnectionTimesOut | negative |
| RFC 9002 | 6.2 | A dropped datagram is retransmitted and the handshake still completes | QuicSimulatorTests.ADroppedDatagramIsRetransmittedAndTheHandshakeCompletes | pass |
| RFC 9002 | 6.2 | Reordered datagrams are not treated as a failed handshake | QuicSimulatorTests.ReorderedDatagramsStillCompleteTheHandshake | pass |
| RFC 9002 | 6.2 | A duplicated datagram does not close the connection | QuicSimulatorTests.ADuplicatedDatagramDoesNotCloseTheConnection | pass |
| RFC 9000 | 14.3 | A datagram above the simulator MTU is dropped and one at 1200 is delivered | QuicSimulatorTests.AnMtuOf1400DropsTheOversizedDatagram | pass |
| RFC 9000 | 14.3 | A path MTU of 1400 settles between 1200 and 1400, and a black hole returns the ceiling to 1200 | QuicPathMtuTests.APathOf1400SettlesBetween1200And1400 | pass |
