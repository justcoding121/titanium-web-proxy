The managed TLS and QUIC stacks use the runtime's platform cryptography where they can. The protocol state machines are ours, and X25519 and ChaCha20-Poly1305 are not FIPS-approved algorithms. The stacks are not a FIPS-validated module.

`FipsEnvironment.IsEnabled()` is the only detector. It reads the Windows `FipsAlgorithmPolicy\Enabled` value and Linux `/proc/sys/crypto/fips_enabled`. macOS is never reported as FIPS mode.

When that detector is on and a managed flag is on, `ProxyServer.Start` and `TwpConfigValidator` refuse. The error names `server.managedStackFipsOverride` and `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1`. Either one allows the stack to run. The handshake then uses `TlsAlgorithmPolicy.FipsApprovedOnly` (AES-GCM, secp256r1, secp384r1, RSA-PSS, and ECDSA P-256/P-384). A browser fingerprint will not match. Startup logs that once.

A handshake also refuses on its own unless `TlsStreamOptions.AllowManagedStackUnderFips`, `AllowManagedStackUnderFips` on the QUIC options, or the environment variable is set. The proxy copies `ProxyServer.AllowManagedStackUnderFips` onto those options.
