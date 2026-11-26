Rows are added as each task's vectors land. A row names the requirement, the code that implements it, and the test that checks it.

| Requirement | Code | Test |
| --- | --- | --- |
| RFC 8446 section 7.1 HKDF-Expand-Label and Derive-Secret | `Internal/TlsHkdf.cs` | `HkdfTests`, `HkdfRfc8448Tests.ExpandLabelMatchesEveryRfc8448Section3To5Output` |
| RFC 8446 section 4.4.1 transcript hash, binder `HashWith`, HelloRetryRequest `message_hash` | `Internal/TranscriptStore.cs` | `HkdfTests.TranscriptHashesAppendedMessagesAndReplacesClientHelloWithMessageHash`, `HkdfRfc8448Tests.ReplaceWithMessageHashMatchesRfc8448Section5Transcript` |
| RFC 5869 HKDF extract and expand, cases 1 to 3 | `TlsHkdf.Extract` and `HKDF.Expand` | `HkdfTests.BclHkdfMatchesRfc5869Case1` through `Case3` |
| RFC 8446 section 7.1 key schedule, traffic keys, finished, exporter, resumption | `Internal/TlsKeySchedule.cs` | `KeyScheduleTests` |
