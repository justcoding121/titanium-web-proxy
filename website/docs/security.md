# Security considerations

## HTTPS decryption (MITM)

Decrypting HTTPS requires a root certificate that clients trust. Only install generated roots on machines you control. Machine-wide trust is stronger and riskier than per-user trust.

## Shared secrets and Plus

- Never commit real `plus.controlPlane.sharedSecret` values.
- Bind the control plane to loopback unless you intentionally expose it behind a locked-down network.
- Treat dashboard and control-plane ports as privileged.

## Certificates on disk

Keep private keys out of git and out of the public website. ACME email/domain in sample configs are placeholders.

## Upstream and auth

Titanium supports proxy authentication, mutual TLS, Kerberos, and NTLM. Configure the minimum privilege needed for your environment.

## Further reading

- [Security-Considerations wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Security-Considerations)
- [Plus](/docs/plus) for CIDR allow-lists, JWT/OIDC, and thin WAF options
- [Editions](/docs/editions) for license boundaries
