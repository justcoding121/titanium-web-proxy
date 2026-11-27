Chrome 158.0.8127.89 on Windows x64, captured 26 November 2026.

These files are the first TLS record Chrome sent to `https://localhost/` from a fresh profile. They contain a ClientHello: random bytes and public key shares only. There are no private keys, cookies, or session secrets.

`tcp/hello-001.bin` through `tcp/hello-050.bin` are the 50 TCP captures. QUIC Initial datagrams are not in this set: with `--enable-quic` and `--origin-to-force-quic-on`, Chrome sent no UDP datagram to the loopback listener.

Regenerate with `tools/managed-stack/Capture-ClientHello.ps1`. Summarize with `tools/managed-stack/Summarize-ClientHello.ps1`.
