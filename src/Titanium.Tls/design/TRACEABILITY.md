Rows are `RFC | section | requirement | test id | status`. A test id is `Class.Method`. The guard test fails when a row names a missing test, or a test carries `RfcRequirementAttribute` for an id with no row.

| RFC | Section | Requirement | Test id | Status |
| --- | --- | --- | --- | --- |
| RFC 8446 | 7.1 | HKDF-Expand-Label prepends tls13 and matches the RFC 8448 traces | HkdfRfc8448Tests.ExpandLabelMatchesEveryRfc8448Section3To5Output | pass |
| RFC 8446 | 4.4.1 | HelloRetryRequest replaces ClientHello1 with message_hash | HkdfRfc8448Tests.ReplaceWithMessageHashMatchesRfc8448Section5Transcript | pass |
| RFC 5869 | 2 | Extract and Expand match cases 1 to 3 | HkdfTests.BclHkdfMatchesRfc5869Case1 | pass |
| RFC 8446 | 7 | The key schedule, traffic keys, exporter, and resumption match RFC 8448 section 3 | KeyScheduleTests.MasterTrafficKeysAndResumptionMatchRfc8448Section3 | pass |
| RFC 8446 | 5 | Records decrypt in place and reject a bad tag, a replay, and overflow | RecordProtectorTests.RejectsTamperReplayOverflowAndEmptyInner | pass |
| RFC 7748 | 5.2 | X25519 scalar multiplication matches the published vectors | X25519Tests.ScalarMultMatchesRfc7748Section52 | pass |
| RFC 8446 | 5.2 | A bad tag, bad padding, or empty inner plaintext is rejected | RecordProtectorTests.RejectsTamperReplayOverflowAndEmptyInner | negative |
| RFC 8446 | 5.1 | A plaintext record longer than 2^14 bytes is record_overflow | RecordProtectorTests.PlaintextHeaderRejectsOverflowAndAcceptsOneChangeCipherSpec | negative |
| RFC 8446 | 4.1.3 | A downgrade sentinel is illegal_parameter | ClientHandshakeTests.ServerHelloNegativesFailClosed | negative |
| RFC 8446 | 4.1.4 | A HelloRetryRequest that does not change the key share is illegal_parameter | ClientHandshakeTests.HelloRetryWithoutAChangeIsIllegal | negative |
| RFC 8446 | 4.2 | A response extension the client did not offer is unsupported_extension | ClientHandshakeTests.UnsolicitedEncryptedExtensionIsRejected | negative |
| RFC 8446 | 4.2.8 | A ServerHello without the selected key share is missing_extension | ClientHandshakeTests.ServerHelloNegativesFailClosed | negative |
| RFC 8446 | 4.4.4 | A Finished that does not match is decrypt_error | ClientHandshakeTests.WrongFinishedIsDecryptError | negative |
| RFC 8446 | 5.1 | A truncated handshake record does not throw | CorpusRunnerTests.TruncationsAndMutationsStayInsideTheDocumentedResult | negative |
| RFC 8446 | 5.1 | A handshake message above the cap is decode_error | EngineTests.OversizedHandshakeMessageIsRejected | negative |
| RFC 8446 | 4.6.3 | More than 32 KeyUpdate messages close the connection | ClientHandshakeTests.KeyUpdateFloodCloses | negative |
| RFC 8446 | 5 | A second ChangeCipherSpec is unexpected_message | ClientHandshakeTests.SecondChangeCipherSpecIsUnexpected | negative |
| RFC 8446 | 6.1 | Handshake bytes after close_notify are not processed | ClientHandshakeTests.HandshakeAfterCloseNotifyIsIgnored | negative |
| RFC 8446 | 7 | A stage secret is wiped on the transition and on dispose | KeyScheduleTests.DisposeZeroesEveryStageSecret | negative |
| RFC 8446 | 5 | The record IV is wiped on dispose | RecordProtectorTests.DisposeZeroesTheIv | negative |
