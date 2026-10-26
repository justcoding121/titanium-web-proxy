> For operators who need an opaque UDP relay, and for .NET library users experimenting with CONNECT-UDP.

CONNECT-UDP (RFC 9298) is how a client sends one UDP flow through an HTTP proxy. The proxy opens a connected UDP socket to the target and copies datagrams both ways. It does not decrypt the payload. A QUIC session inside the tunnel stays end to end encrypted.

The listener type is `masque`. It is a TLS endpoint that speaks HTTP/2 (`h2`) and advertises extended CONNECT. The request is:

```http
:method CONNECT
:protocol connect-udp
:scheme https
:path /.well-known/masque/udp/{host}/{port}/
capsule-protocol: ?1
```

Datagrams travel in RFC 9297 capsules on that stream. Context ID 0 is the UDP payload. Any other context is ignored. Unknown capsule types are skipped. A declared length above 2048 bytes is discarded as it arrives and is never buffered.

HTTP/1.1 Upgrade (RFC 9298 section 3.3) stays off unless `allowHttp11Upgrade` is true. With the flag on, the handshake offers `h2` and `http/1.1`. A client sends `GET` on the same path with `Connection: Upgrade`, `Upgrade: connect-udp`, and `Capsule-Protocol: ?1`. The listener answers `101 Switching Protocols` and then reads capsules on that connection until the tunnel ends, then closes it. With the flag off, the handshake offers only `h2`, which is the same as today.

## What this listener does not do

HTTP/3 between the client and the proxy is not available. `System.Net.Quic` has no datagram API (dotnet/runtime#53533), and this listener does not call MsQuic. Capsules on a reliable stream have head-of-line blocking. That is inherent to CONNECT-UDP over HTTP/2.

Existing explicit, transparent, SOCKS, and QUIC listeners still reject `connect-udp` with 501. This listener does not advertise Alt-Svc, and it does not implement CONNECT-IP, SOCKS5 UDP ASSOCIATE, or WebTransport.

Client support for CONNECT-UDP over HTTP/2 is not verified against a shipping MASQUE client. The relay is covered by raw HTTP/2 tests. Treat production use as experimental (`TWP001`) until a real client is confirmed.

## Safe defaults

Authentication is required unless `requireAuth` is false. Put users in `listeners[].masque.credentials`. Each entry has a `username` and a `passwordEnv`, the name of an environment variable that holds the password. The password is never written in the config file. The listener hashes it with SHA-256 and compares in constant time. A missing or empty variable fails startup. With no credentials and `requireAuth` left on, validation fails with "masque listener requires credentials or requireAuth: false". Library users can set `MasqueOptions.AuthenticateFunc` instead; if that is null the listener falls back to `ProxyServer.ProxyBasicAuthenticateFunc`, and if both are null it answers 407. Turning authentication off on a non-loopback address produces a configuration warning.

Allowed target ports default to 443. The tunnel pins one address at open time, and that address is blocked when it is loopback, private, link-local, site-local, multicast, `::`, a documentation or benchmark range (`192.0.0.0/24`, `192.0.2.0/24`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`), or an IPv6 form that embeds one of those (`::ffff:0:0/96`, NAT64 `64:ff9b::/96`, 6to4 `2002::/16`, Teredo `2001::/32`). A later DNS change cannot move the socket. Relax either control explicitly in configuration. This policy fails closed.

Caps default to 16 tunnels per client address and 1024 per listener. Each tunnel also has a one-second token bucket (1000 datagrams and 1,000,000 bytes). The idle timer defaults to 120 seconds and resets on traffic in either direction. A full client-bound queue drops datagrams. UDP send failures are counted and do not close the tunnel.

```yaml
listeners:
  - type: masque
    host: 0.0.0.0
    port: 443
    masque:
      requireAuth: true
      allowedPorts: [443]
      blockPrivateTargets: true
      maxTunnels: 1024
      maxTunnelsPerClient: 16
      idleTimeoutSeconds: 120
      maxDatagramsPerSecond: 1000
      maxBytesPerSecond: 1000000
```

In code, add a `MasqueProxyEndPoint` and set `GenericCertificate` to a certificate clients trust. When the certificate is left unset, the listener generates a leaf for `localhost` from the proxy certificate manager.

## HTTP/3 later

The relay talks to an internal datagram channel. Today's channel reads and writes capsules. A later build can add a QUIC datagram channel behind the same channel once .NET exposes datagrams, without changing target policy or the UDP socket. Direct MsQuic interop stays out: it would take ownership of the native QUIC callbacks that `System.Net.Quic` already owns.
