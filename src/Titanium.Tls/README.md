# Titanium.Tls

Managed TLS 1.3 for .NET. The protocol state machine is C#. Cryptography calls the runtime's `System.Security.Cryptography` implementations, except for the algorithms the runtime does not provide on every platform.

This library is experimental. It is not a FIPS-validated module. Enable it from Titanium Web Proxy only through the managed-stack flag. With that flag off, the proxy keeps using `SslStream`.
