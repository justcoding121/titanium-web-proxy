# Parity differences

Expected differences between `SslStream` and `TlsStream`. An entry needs a reason. A divergence that is not listed here fails the parity suite.

Seeded from the S1 `SslStream` baselines (Windows x64 and Linux x64, runtime 10.0.15, TLS 1.3, `TLS_AES_256_GCM_SHA384`, self-signed RSA, validation callback returns true). macOS did not complete this handshake within 30 seconds, so it is not a baseline yet.

| Scenario | SslStream result |
| --- | --- |
| TCP reset without close_notify | `ReadAsync` throws `IOException` |
| Read after dispose | `ObjectDisposedException` |
| Zero-length write | Completes |
| Zero-length read | Does not return immediately. A 200 ms cancellation throws `OperationCanceledException` |
| Write after `ShutdownAsync` | `InvalidOperationException` ("Write operations are not allowed after the channel was shutdown.") |
| Read cancelled while blocked | `OperationCanceledException` |
| Concurrent client write and server read | The server reads the 4 bytes |

No managed-stack divergence is recorded yet. T1.12 fills this table.
