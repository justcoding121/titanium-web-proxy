# Titanium.Tls

Managed TLS 1.3 for .NET. The protocol state machine is C#. Cryptography calls the runtime's `System.Security.Cryptography` implementations, except for the algorithms the runtime does not provide on every platform.

This library is experimental. It is not a FIPS-validated module. Titanium Web Proxy does not call it yet, so proxy traffic stays on `SslStream`. Local timing is in `design/PROGRESS.md`.
