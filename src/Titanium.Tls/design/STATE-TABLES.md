Client and server transitions for the synchronous handshake in `TlsEngine`. A message the current state does not list is `unexpected_message` (10), after which the engine is `Closed` and further input is ignored. `Resume` is legal only in a decision state. Sources: `src/Titanium.Tls/Internal/TlsEngine.cs`, `TlsEngine.Client.cs`, `TlsEngine.Server.cs`.

## Client

| State | Accepts | On success |
| --- | --- | --- |
| `Start` | no handshake message; `Advance` with an empty buffer writes the ClientHello | `WaitServerHelloOrHrr` |
| `WaitServerHelloOrHrr` | ServerHello, including HelloRetryRequest | a retry returns to `WaitServerHelloOrHrr`; otherwise `WaitEncryptedExtensions` |
| `WaitEncryptedExtensions` | EncryptedExtensions | `WaitCertificateRequestOrCertificate` |
| `WaitCertificateRequestOrCertificate` | CertificateRequest or Certificate | a request goes to `WaitCertificate`; a certificate goes to `WaitCertificateDecision` |
| `WaitCertificate` | Certificate | `WaitCertificateDecision` |
| `WaitCertificateDecision` | no message; `Resume` | `Resume(true)` goes to `WaitCertificateVerify`; `Resume(false)` is `bad_certificate` (42) |
| `WaitCertificateVerify` | CertificateVerify | `WaitFinished` |
| `WaitFinished` | Finished | `Connected`, after the client writes its Certificate (when requested), CertificateVerify (when it has a certificate), and Finished |
| `Connected` | NewSessionTicket | stays `Connected` |
| `Connected` | KeyUpdate | stays `Connected` and rotates the read keys. `update_requested` sets a write-side response |
| `Closed` | nothing | input is ignored |

A second HelloRetryRequest is `unexpected_message`. An HelloRetryRequest that selects the group the client already shared is `illegal_parameter` (47), whether or not it carries a cookie. The second ClientHello must keep the same fields except `key_share`, `cookie`, `padding`, a removed `early_data`, and the recomputed PSK binder. A missing share for the requested group is `illegal_parameter`.

## Server

| State | Accepts | On success |
| --- | --- | --- |
| `WaitClientHello` | ClientHello | `WaitClientHelloDecision` |
| `WaitClientHelloDecision` | no message; `Resume` | `Resume(false)` is `handshake_failure` (40). A callback exception, or `Resume(true)` with no selection, is `internal_error` (80). Otherwise the server writes its flight and goes to `WaitFinished`, or to `WaitClientHello` after one HelloRetryRequest |
| `WaitClientHello` after a retry | one more ClientHello | the flight, then `WaitClientCertificate` when a certificate was requested, otherwise `WaitFinished` |
| `WaitClientCertificate` | Certificate | an empty list goes to `WaitFinished`; a chain that builds goes to `WaitClientCertificateVerify` |
| `WaitClientCertificateVerify` | CertificateVerify | `WaitFinished` |
| `WaitFinished` | Finished | `Connected`, then one NewSessionTicket when a ticket protector was supplied |
| `Connected` | KeyUpdate | stays `Connected` and rotates the read keys. `update_requested` sets a write-side response |

The server does not accept a post-handshake Certificate. Early data is not negotiated.

## Alerts

| Condition | Alert |
| --- | --- |
| Message not legal in the current state, a second retry, CCS before the first ClientHello or in the middle of a handshake message, an empty handshake or alert record, or more than 32 consecutive empty application records | `unexpected_message` (10) |
| A plaintext record longer than 2^14 bytes, or an inner plaintext longer than 2^14 + 1 | `record_overflow` (22) |
| No common cipher or group, a client certificate the client cannot sign, or `Resume(false)` on the server hello | `handshake_failure` (40) |
| Client certificate chain does not build for `clientAuth`, or the caller rejects the server certificate | `bad_certificate` (42) |
| Version, cipher, group, session id, downgrade sentinel, ALPN selection, ticket lifetime, or a CertificateVerify scheme this side did not offer | `illegal_parameter` (47) |
| Handshake body above 128 KiB, or a message that does not parse | `decode_error` (50) |
| CertificateVerify signature or Finished verify_data does not match | `decrypt_error` (51) |
| `supported_versions` does not contain `0x0304` | `protocol_version` (70) |
| Callback throws, or the callback returns no certificate | `internal_error` (80) |
| ServerHello has no `key_share` | `missing_extension` (109) |
| Client offered ALPN, nothing matches, and the callback requires a match | `no_application_protocol` (120) |

A ServerHello legacy_version other than `0x0303` is `illegal_parameter`. A missing `supported_versions` extension aborts the client with `TlsVersionNotSupportedException` and sends no alert, unless the random ends with the downgrade sentinel, which is `illegal_parameter`. A present `supported_versions` value other than `0x0304`, or a TLS 1.3 ServerHello that still carries the sentinel, is `illegal_parameter`. A malformed value is `decode_error`. A record version other than `0x0301` or `0x0303` is `unexpected_message`. One ChangeCipherSpec (`0x01`) is ignored after the first ClientHello and before `Connected`, and only between handshake messages. A second one, one before the first ClientHello, or one after `Connected`, is `unexpected_message`. An alert the peer sends after the handshake keys exist is a `PeerAlert` and is not answered. A plaintext alert after those keys exist is `unexpected_message`. Alerts this stack sends after the peer can read them are protected.

## Checks

- `supported_versions` must contain `0x0304` (RFC 8446 section 4.2.1). The client aborts a ServerHello that is not TLS 1.3.
- The last 8 bytes of a ServerHello random must not be the downgrade sentinel (section 4.1.3).
- HelloRetryRequest happens at most once, and the following ClientHello must differ (section 4.1.4). The client checks that the selected group was not already shared.
- The client requires `key_share` on the ServerHello. A missing share is `missing_extension` (section 4.2.8). If the client's share matches none of the server's groups, and the client advertised one of those groups, the server sends one HelloRetryRequest. Otherwise the server sends `handshake_failure`.
- CertificateVerify uses the context string for the direction that signed it, 64 bytes of `0x20`, a zero, then the transcript hash (section 4.4.3). The server offers and accepts only `ecdsa_secp256r1_sha256` (`0x0403`) for the client. The server's own signature is `0x0403`, `0x0503`, or `rsa_pss_rsae_sha256` (`0x0804`), chosen from the key and the client's list.
- Finished is HMAC over the transcript hash through CertificateVerify, compared with `FixedTimeEquals` (section 4.4.4).
- An empty client Certificate is legal and is not followed by CertificateVerify (section 4.4.2). A non-empty one must chain for `clientAuth`. Hostname is not checked on a client certificate. A failure is `bad_certificate` and does not pause.
- The server certificate is checked for `serverAuth` and the hostname, then the client pauses. Stapled OCSP (`status_request`) and SCTs are copied and not enforced. Revocation mode is `NoCheck`.
- A NewSessionTicket lifetime above 604800 seconds is `illegal_parameter` (section 4.6.1). The ticket this server writes uses 6 hours. Resumption also requires the binder, cipher, server name, ALPN, and leaf SHA-256.
- KeyUpdate rotates only the read traffic secret. A request is answered on the write path, under the old write key, before that direction rotates. An empty KeyUpdate body is `illegal_parameter`.
- A plaintext record longer than 2^14 bytes is `record_overflow`. A ciphertext record may be 2^14 + 256 bytes; the AEAD check still rejects an inner plaintext above 2^14 + 1.
- A handshake message must end the record when it installs new keys. Bytes left in that record are `unexpected_message`. That covers ServerHello and the peer Finished on the client, and ClientHello and the client Finished on the server.
- A known extension on a message that does not use it is `illegal_parameter`. A response extension this client did not offer is `unsupported_extension` (110), except `cookie` on HelloRetryRequest.
- The server Certificate context must be empty. An empty server certificate list is `decode_error`. The client Certificate must use the empty context this server requested.
- CertificateRequest must use an empty context and must carry `signature_algorithms`. A missing list is `missing_extension`. A client that cannot sign with `ecdsa_secp256r1_sha256` sends an empty Certificate and no CertificateVerify.
- `legacy_compression_methods` must be a single zero byte. A ClientHello without `supported_groups` or `signature_algorithms` is `missing_extension`.
- A PSK binder that does not match a ticket this server would accept is `decrypt_error`. A different number of identities and binders is `illegal_parameter`. `pre_shared_key` without `psk_key_exchange_modes` is `missing_extension`. An unknown or expired ticket still falls back to a full handshake.
- If the client offered `early_data` and the server did not accept it, application records that fail under the handshake key are skipped, up to 16 KiB, until one record decrypts.

## Known limits

The engine hello offers one cipher (`0x1301`) and two groups (`0x001D`, `0x0017`). It is not the Chrome profile and it is not the RFC 8448 hello. The RFC 8448 section 3 CertificateVerify and Finished are checked against the published messages, not by replaying that hello through this state machine. Online revocation runs inline. `CryptoExecutionPolicy` does not hop threads.
