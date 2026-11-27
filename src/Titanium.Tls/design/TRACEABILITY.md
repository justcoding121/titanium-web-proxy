Rows are `RFC | section | requirement | test id | status`. A test id is `Class.Method`. The guard test fails when a row names a missing test, or a test carries `RfcRequirementAttribute` for an id with no row.

| RFC | Section | Requirement | Test id | Status |
| --- | --- | --- | --- | --- |
| RFC 8446 | 7.1 | HKDF-Expand-Label prepends tls13 and matches the RFC 8448 traces | HkdfRfc8448Tests.ExpandLabelMatchesEveryRfc8448Section3To5Output | pass |
| RFC 8446 | 4.4.1 | HelloRetryRequest replaces ClientHello1 with message_hash | HkdfRfc8448Tests.ReplaceWithMessageHashMatchesRfc8448Section5Transcript | pass |
| RFC 5869 | 2 | Extract and Expand match cases 1 to 3 | HkdfTests.BclHkdfMatchesRfc5869Case1 | pass |
| RFC 8446 | 7 | The key schedule, traffic keys, exporter, and resumption match RFC 8448 section 3 | KeyScheduleTests.MasterTrafficKeysAndResumptionMatchRfc8448Section3 | pass |
| RFC 8446 | 5 | Records decrypt in place and reject a bad tag, a replay, and overflow | RecordProtectorTests.RejectsTamperReplayOverflowAndEmptyInner | pass |
| RFC 7748 | 5.2 | X25519 scalar multiplication matches the published vectors | X25519Tests.ScalarMultMatchesRfc7748Section52 | pass |
