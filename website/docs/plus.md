# Plus

Optional ops plugin for the CLI (and Inspector panels). For operators who want a dashboard, metrics, auth, and a thin web application firewall (WAF) without writing custom code.

**Next:** install with `titanium update --plus`, enable in YAML, open the dashboard.

## What you get

| Outcome | Capability |
|---------|------------|
| Dashboard | Sign-in page and six views (overview, routes and clusters, health, config, certificates, audit) on an ephemeral port (or explicit `controlPlane.dashboardPort`); see [Dashboard](#dashboard) |
| Observability | Prometheus-style metrics for destination state / latency |
| Security | CIDR allow-list, JWT/OIDC (JWKS), API key / Basic auth |
| Login | `oidc` route policy: OpenID Connect authorization-code login with PKCE, encrypted session cookie, silent refresh, and claim headers; see [Login with OpenID Connect](#login-with-openid-connect-oidc-policy) |
| Web application firewall (WAF) | Thin deny-list (paths, methods, headers, body size) — not a full WAF suite |
| Control plane | Loopback-default HTTP(S) API with shared-secret header; snapshot get/put; cache purge; optional TLS; audit trail; failed-login throttle |
| Operations | Drain / healthy / maintenance destination states |
| Discovery | File watch, DNS poll; Consul blocking queries; Kubernetes EndpointSlice list-then-watch with backoff |
| State | Fixed-window per-IP rate limit (`state.mode=memory` or `state.redis`) |
| Resilience | Active HTTP/TCP health probes, circuit outlier ejection, bounded idempotent connection retries |
| CORS | Opt-in preflight + `Access-Control-*` response headers |
| Cache | In-memory RFC 9111 response cache with Vary, revalidation, size limits and request collapsing (`cache.enable`; see [Configuration](/docs/configuration#response-cache)) |
| gRPC-JSON transcoding | REST/JSON ↔ gRPC (unary + multi-frame streaming; optional gzip) via FileDescriptorSet + `google.api.http` ([guide](/docs/grpc-json-transcoding)) |

File-based route/cluster reload (`titanium reload` / SIGHUP / `run --watch`) does **not** require Plus — use the control plane when you want programmatic snapshot updates or discovery watches.

## Install

Plus is distributed as a sidecar next to the CLI. There is **no** public Plus download button on this site.

```shell
titanium update --plus
titanium version --check --plus
# Prerelease / beta channel:
titanium update --plus --channel beta
titanium version --check --plus --channel beta
```

`titanium version --check --plus` reports local → remote and exit code `2` when a newer Plus is available or Plus is missing. `titanium update --plus` installs or upgrades only when needed; if Plus is already current it prints that and skips the download.

## Enable / disable

Day-to-day control is config — keep Plus installed and toggle features:

```yaml
plus:
  enabled: true
  controlPlane:
    host: "127.0.0.1"
    port: 9080
    sharedSecret: "<shared-secret>"
  options:
    cache.enable: "true"
```

Set `plus.enabled: false` (or remove the `plus:` block) to stop using Plus without deleting it from disk.

Use a strong secret in production. Dev-only default secrets require an explicit environment opt-in on loopback.

**Engine knobs** (profiles, timeouts, TLS, limits, upstream, …) are configured under `server:` in twp.yaml — not `plus.options`. See [Configuration](/docs/configuration).

## Remove

To delete Plus from disk:

```shell
titanium update --remove-plus
```

This removes Plus beside the CLI (and `.bak` / `.new`). It does **not** stop a running proxy, OS service, or the in-process Plus control plane / dashboard — stop `titanium run` or `titanium service stop`, then start again so Plus unloads. It does not edit your config; disable `plus.enabled` separately if it is still set. Re-install later with `titanium update --plus`.

## Dashboard

The dashboard is server-rendered HTML plus one small script that polls `/api/*` every 2 seconds. There is no front-end build and no external asset: the page loads `/assets/dashboard.js` and `/assets/dashboard.css` from the same origin. Its address is printed at start-up; set `plus.controlPlane.dashboardPort` to pin the port.

| View | Path | What it shows |
|------|------|---------------|
| Overview | `/` | Requests per second (last 10 s), error ratio and p50 / p95 / p99 latency (last 60 s, from the proxy's duration histogram), active connections and requests |
| Routes and clusters | `/routes` | Read-only route and cluster config with secrets masked, per-route requests, errors and latency, and a Drain / Healthy button for each destination |
| Health and ejections | `/health` | The last 200 passive and circuit-breaker ejections, active-probe failures and recoveries |
| Config and reload | `/config` | Schema version, config hash, when the config loaded, and the time, result and (redacted) error of the last reload |
| Certificates | `/certificates` | Subject, issuer, expiry and days left for the listener, SNI, client-CA and upstream certificate files |
| Audit | `/audit` | The newest audit events |

The same data is served as JSON at `/api/overview`, `/api/routes`, `/api/health`, `/api/config`, `/api/certs`, `/api/audit` and `/api/snapshot`, and as Prometheus text at `/metrics`.

Per-route counters cover at most 1024 routes; further routes are counted under `(other)`. A route's match values (header and query values) are never shown, and transform parameters are shown only for a fixed list of non-sensitive keys.

### Sign-in and roles

Opening a page without a session redirects to `/login`. Signing in with a secret sets the `titanium_plus_session` cookie: a random 256-bit id, `HttpOnly`, `SameSite=Strict` (and `Secure` when TLS is on). A session ends after 30 minutes without use or on **Sign out**; the script's own polling does not count as use. At most 64 sessions are kept, and the least recently used one is dropped to make room.

| Role | Secret | Can do |
|------|--------|--------|
| operator | `plus.controlPlane.sharedSecret` | Everything, including Drain and Healthy |
| viewer | `plus.controlPlane.dashboardViewerSecret` | Read every view; the buttons are disabled and the server answers `403` to a mutation |

`dashboardViewerSecret` takes the same `...Env` / `...File` forms as the shared secret (see [Secret references](/docs/configuration#secret-references)). It must differ from the shared secret and may not be a well-known default.

Every change a session makes (Drain, Healthy, Sign out) needs the CSRF token the page carries in the `X-Titanium-Csrf` header; a request without it gets `403` and changes nothing. Scripts and browser-automation tests can skip the cookie and send the `X-Titanium-Control-Secret` header on each request instead: that header is the credential, so it needs no CSRF token. A wrong secret at the login page or in the header counts toward the [failed-login throttle](#failed-login-throttle); a request carrying an expired cookie does not.

Every response carries `Content-Security-Policy: default-src 'self'` (no inline script or style), `X-Content-Type-Options: nosniff`, `Cache-Control: no-store` and `Referrer-Policy: no-referrer`. Values from your config, such as a destination id, are HTML-encoded.

Editing routes from the dashboard and single sign-on are not provided.

## Control-plane and credential hardening

### Hashed Basic users

`security.basicUsers` takes `user:secret` pairs separated by commas. The secret may be:

| Form | Example |
|------|---------|
| bcrypt | `alice:{bcrypt}$2b$12$...` (`$2a$`, `$2b$`, and `$2y$` are accepted) |
| PBKDF2 | `bob:{pbkdf2}210000$<salt-base64>$<hash-base64>` |
| plaintext | `carol:s3cret`: still works, but Titanium logs a start-up warning that names the user (never the password) |

PBKDF2 here is PBKDF2-HMAC-SHA256 with a 16-byte or longer salt and a 16 to 64 byte hash; new hashes use 210000 iterations. A hash that does not parse stops start-up with a message that names the user, so a typo cannot leave a user silently locked out or unprotected. Every secret comparison (Basic passwords, API keys, the control-plane shared secret) is constant-time. A verified hashed login is remembered in memory for five minutes, so a keep-alive client does not pay the hashing cost on every request.

Put the list in a file with one `user:secret` per line and point `security.basicUsersFile` at it (see [Secret references](/docs/configuration#secret-references)). `security.basicUsersEnv` and `discovery.consulTokenFile`/`Env`, `state.redisFile`/`Env`, `security.apiKeysFile`/`Env`, `plus.controlPlane.sharedSecretFile`/`Env`, and `plus.controlPlane.dashboardViewerSecretFile`/`Env` all work the same way. The Redis connection string is never written to logs; only its host and port are.

### Audit log

Set `audit.path` to keep an append-only record of what happened on the control plane and the dashboard. Each line is one JSON object:

```json
{"time":"2026-01-02T03:04:05.0000000Z","actor":"dashboard:shared-secret","remote":"10.1.2.3","action":"drain","target":"d1","result":"ok"}
```

| Field | Meaning |
|-------|---------|
| `time` | UTC, ISO 8601 |
| `actor` | A role label such as `control-plane:shared-secret`, `dashboard:operator`, `dashboard:viewer`, `dashboard:shared-secret` (header), or `...:anonymous` for a failed login. It never contains a secret or a presented credential |
| `remote` | Client address |
| `action` | `drain`, `healthy`, `snapshot-put`, `cache-purge`, `login` (a dashboard sign-in, or a first page opened with the secret header), `logout`, or `login-failed` |
| `target` | A destination id, path, or purge prefix, at most 256 characters |
| `result` | `ok`, `denied`, `throttled`, `rejected`, `unavailable`, or `error` |

The file rolls by size: when it would pass `audit.maxBytes` it becomes `audit.path.1`, older files shift up, and files past `audit.maxFiles` are deleted. A write failure never fails the request; it is counted and reported once in the log. The dashboard shows the newest 50 events and serves up to 500 (a bounded in-memory ring) as JSON at `/api/audit`, newest first. Without `audit.path` only that ring is kept.

### Failed-login throttle

A remote that fails authentication five times in one minute gets `429 Too Many Requests` with `Retry-After` until its oldest failure ages out, even if it then sends the right secret. The control plane and the dashboard share one count. API-key and Basic auth on the proxy path apply the same limit with a count of their own (a refused request there gets `429` and `Retry-After` too). At most 10000 remotes are tracked; when the table is full, expired entries go first, then the least recently failing one. A lockout is written to the audit log once, not once per refused request.

### TLS and non-loopback listeners

The control plane and dashboard listen on `plus.controlPlane.host` (default `127.0.0.1`). On any other address they must be encrypted:

```yaml
plus:
  enabled: true
  controlPlane:
    host: "0.0.0.0"
    port: 9080
    sharedSecretFile: /run/secrets/control-secret
    tls:
      certificatePath: /etc/titanium/tls/tls.crt   # PEM certificate (chain)
      keyPath: /etc/titanium/tls/tls.key           # PEM private key
```

Both listeners then serve HTTPS from that certificate. `titanium test` and `titanium run` refuse a non-loopback host without `tls`, unless you set `allowInsecureNonLoopback: true` for a network that already encrypts and isolates the port (a service mesh, a locked-down pod network). That opt-out is allowed but prints a warning, because the shared secret then crosses the network in clear text. Relative certificate paths resolve against the config directory; a certificate change needs a restart.

On Kubernetes the Helm chart listens on `0.0.0.0` in the pod, so set `plus.controlPlane.tls.existingSecret` to a `kubernetes.io/tls` Secret (mounted at `/etc/titanium-plus-tls`) or `plus.controlPlane.allowInsecureNonLoopback=true`; the chart refuses to render with neither. See [Kubernetes](/docs/kubernetes).

## Login with OpenID Connect (`oidc` policy)

A route that lists an `oidc` policy sends an unauthenticated browser to your identity provider, takes it back through the authorization-code flow with PKCE, and then keeps the person signed in with an encrypted cookie. The origin sees only the requests of people who signed in, plus the claims you choose to pass on as headers.

The policy needs Plus (`plus.enabled: true`; `titanium validate` refuses it otherwise). If Plus is not loaded, or the policy cannot start (an unset secret, a key of the wrong size), every request to the route is answered `503`. It never lets a request through unprotected. A route without an `oidc` policy pays nothing.

```yaml
plus:
  enabled: true
policies:
  - name: login
    kind: oidc
    issuer: https://idp.example.com/realms/main
    clientId: titanium
    clientSecretEnv: OIDC_CLIENT_SECRET
    cookieSecretEnv: OIDC_COOKIE_KEY       # 32 bytes: 64 hex digits or base64
    allowedEmailDomains: [example.com]
    allowedGroups: [staff]
    passClaims:
      email: X-User-Email
      sub: X-User-Id
    logoutPath: /logout
routes:
  - id: app
    clusterId: web
    match: { path: /, pathKind: Prefix }
    policies: [login]
```

Register `https://<your host>/_titanium/oidc/callback` (or your `redirectPath`) as a redirect URI with the provider.

| Field | Meaning |
|-------|---------|
| `issuer` | Provider URL. The discovery document at `<issuer>/.well-known/openid-configuration` must name the same issuer. `https` only; plain `http` is accepted for a loopback host, for local development |
| `clientId`, `clientSecretEnv` or `clientSecretFile` | The client. Leave the secret out for a public client. The secret is sent with HTTP Basic, or in the form body when the provider lists only `client_secret_post` |
| `cookieSecretEnv` or `cookieSecretFile` | The key that seals the cookies: 32 bytes as 64 hex digits or base64. List up to four, separated by commas or one per line, to rotate: the first key seals, every key opens, so sessions made under the old key survive until you drop it |
| `redirectPath` | Callback path. Default `/_titanium/oidc/callback` |
| `publicBaseUrl` | Origin used to build the redirect URI, such as `https://app.example.com`. Without it the request's `Host` header is used. Set it when the proxy sits behind something that rewrites the host |
| `scopes` | Default `openid email profile`, plus `offline_access` with `refreshTokens`. `openid` is always requested |
| `cookieName` | Session cookie name. Default `titanium_oidc`. Login-in-progress cookies use `<cookieName>_f<hash>` |
| `sessionTtlSeconds` | Hard limit on a session, 60 to 2592000. Default 28800 (8 hours) |
| `allowedEmails`, `allowedEmailDomains` | The person's e-mail must match an address or a domain (exact, not a suffix). An address the provider marks `email_verified: false` never matches |
| `allowedGroups`, `groupsClaim` | The person must be in one of these groups. `groupsClaim` names the claim that lists them (default `groups`; a dotted name reads a nested claim) |
| `passClaims` | Map of claim name to request header, for example `email: X-User-Email`. The proxy removes any client-sent header of that name first, so a client cannot forge it. Headers that carry meaning of their own (`Authorization`, `Cookie`, `Host`, and similar) are refused |
| `logoutPath` | A request to this path clears the session and redirects to `rd` (when allowed) or `/`. It is answered by the proxy and never forwarded |
| `postLoginRedirectAllowlist` | Origins (`https://app.example.com`) that a `?rd=` value may name, after sign-in and sign-out. Local paths are always allowed |
| `refreshTokens` | Renew the session quietly shortly before the ID token expires, using the refresh token |

When both an e-mail rule and a group rule are set, both must hold.

**Flow.** A browser request without a valid session is redirected to the provider with `response_type=code`, `code_challenge_method=S256`, a random `state`, and a random `nonce`. The state, nonce, PKCE verifier, and return target travel in a short-lived (10 minute) cookie, encrypted and authenticated with the same cookie key, one per login so several tabs can sign in at once. The callback must carry the state that matches that cookie, and a state can be used once. The ID token must carry a valid signature (RSA, RSASSA-PSS, or ECDSA keys from the provider's JWKS; shared-secret algorithms and unsigned tokens are refused), the right `iss`, the client as `aud` (and a matching `azp` when there are several audiences), an unexpired `exp` with 60 seconds of clock-skew allowance, and the `nonce` that was sent.

**Session cookie.** AES-256-GCM, with a key derived per purpose and the cookie name bound into the authentication, so a cookie cannot be moved to another name or used for another purpose. It is `HttpOnly`, `Secure`, `SameSite=Lax`, and `Path=/`, and holds the person's subject, e-mail, the groups a rule names, the claims you pass on, and (with `refreshTokens`) the refresh token. It never holds the ID token or the access token. The sealed value has to stay under 4 KB. When it would not fit, the proxy first leaves the refresh token out (the session then ends with the ID token), and if it still does not fit it answers `500` at sign-in rather than truncate: trim `passClaims` or `allowedGroups`. The cookie is `Secure`, so serve the route over HTTPS. The origin never sees the session cookie or the login-in-progress cookies.

**Requests that cannot follow a redirect.** A request with `Accept: application/json` (and no `text/html`) or an `X-Requested-With` header, and any method other than `GET` or `HEAD`, gets `401` with `{"error":"unauthenticated"}` instead of a redirect, so scripts can handle it.

**Refresh.** With `refreshTokens: true` the proxy redeems the refresh token when the ID token is within a minute of expiring, and sends a renewed `Set-Cookie` with the response. Concurrent requests that carry the same session share one call to the provider, and a request that still carries the old cookie for a minute afterwards gets the same answer, so a refresh token that rotates is never redeemed twice. A provider that refuses the refresh ends the session: browsers go back to sign-in, scripts get `401`. While the provider is unreachable the session keeps working until the ID token is more than a minute past expiry, then requests get `503` with `Retry-After`. A provider that issues no refresh token ends the session when the ID token expires.

**Provider documents.** Discovery is cached for an hour and the key set follows. An ID token that names an unknown key id makes the proxy fetch the keys again, but no more often than every 30 seconds. A failed fetch is retried with exponential backoff up to five minutes, and the last good document stays in use meanwhile.

**Safeguards.**

- A post-login or post-logout target is a local path or an allow-listed origin. Anything else (`//host`, `/\host`, other schemes, credentials in the URL, control characters) is dropped, and the browser goes to the page it asked for.
- The callback is limited to 60 requests per minute per client, answers only `GET`, and refuses a callback whose state was already used.
- Secrets, tokens, codes, states, and cookie values are never logged. `titanium run` also redacts the secret values from console output.
- Cookie and client secrets are read when the policy first runs. A reload creates new policies and so reads them again. A missing variable or file is reported when the config loads or reloads.

**Limits.**

- A session is stateless: signing out clears the browser's cookie, but a copy of the cookie stays valid until `sessionTtlSeconds` runs out. Keep the TTL short where that matters, or rotate the cookie key to end every session at once.
- The proxy does not call the provider's end-session endpoint.
- Not available on terminate-lite, which cannot hold a request for a login; a route with an `oidc` policy always uses the full session path.

## `plus.options` keys (reference)

String values under `plus.options` (examples):

| Key | Role |
|-----|------|
| `discovery.mode` | `file`, `dns`, `consul`, or `k8s` |
| `discovery.file` | Path for `mode=file` |
| `discovery.dnsName` / `discovery.dnsPort` | DNS discovery target |
| `discovery.consulUrl` | Consul service endpoint, for example `http://consul:8500/v1/health/service/web` |
| `discovery.consulWaitSeconds` / `discovery.consulPassing` / `discovery.consulToken` | Blocking-query wait (default `30`), passing-only filter (default `true`), ACL token |
| `discovery.k8sUrl` | API server URL. Optional in a pod: `KUBERNETES_SERVICE_HOST` is used. A URL with a path (or without a selector) is used as the exact EndpointSlice collection to watch; a single Endpoints object there is polled every `discovery.intervalMs` |
| `discovery.k8sService` / `discovery.k8sLabelSelector` / `discovery.k8sNamespace` / `discovery.k8sPortName` | Which slices to watch (a selector is required unless `k8sUrl` is used as the exact URL: it has a path or no selector is set); namespace defaults to the pod namespace; named port, default first TCP port |
| `discovery.k8sServiceAccountDir` / `discovery.k8sTokenRefreshSeconds` / `discovery.k8sWatchTimeoutSeconds` | Token and CA directory (default `/var/run/secrets/kubernetes.io/serviceaccount`), token re-read interval (default `60`), watch window (default `300`) |
| `discovery.backoffMinMs` / `discovery.backoffMaxMs` | Retry backoff bounds for `consul` and `k8s` (defaults `1000` and `30000`) |
| `discovery.intervalMs` / `discovery.clusterId` | Poll interval for `dns` and for sources that cannot block or watch; cluster id |
| `security.allowCidrs` | Comma-separated client CIDR allow-list |
| `security.jwtAuthority` / `security.jwtAudience` / `security.jwksUrl` | JWT/OIDC validation |
| `security.apiKeys` | Comma-separated API keys (`X-Api-Key` by default) |
| `security.apiKeyHeader` | Alternate API key header name |
| `security.basicUsers` | Comma-separated `user:secret` pairs for HTTP Basic. `secret` is `{bcrypt}$2b$...`, `{pbkdf2}iterations$salt$hash`, or a plaintext password (works, warns at start-up). See [Control-plane and credential hardening](#control-plane-and-credential-hardening) |
| `audit.path` | Append-only NDJSON audit log for control-plane and dashboard events. Relative paths resolve against the config directory |
| `audit.maxBytes` / `audit.maxFiles` | Size at which the audit file rolls (default `10485760`) and how many rolled files to keep (default `5`) |
| `cors.enabled` | Enable CORS helper (`true`) |
| `cors.allowOrigin` / `cors.allowMethods` / `cors.allowHeaders` / `cors.allowCredentials` / `cors.maxAgeSeconds` | CORS knobs |
| `waf.enabled` | Enable thin deny-list WAF |
| `waf.denyPaths` / `waf.denyMethods` / `waf.denyHeader` | Deny rules |
| `waf.maxBodyBytes` / `waf.rulesFile` | Body cap and optional rules file |
| `state.mode` | `memory` or use Redis via `state.redis` |
| `state.redis` / `state.rateLimitPerMinute` | Redis connection and rate limit |
| `resilience.activeHealth` | Enable active probes |
| `resilience.intervalMs` / `resilience.unhealthyThreshold` / `resilience.path` / `resilience.protocol` / `resilience.timeoutMs` | Probe knobs |
| `resilience.circuit.enabled` | Outlier ejection on consecutive 5xx → Unhealthy |
| `resilience.circuit.failureThreshold` / `resilience.circuit.cooldownMs` | Circuit knobs |
| `resilience.retry.idempotentAttempts` | Raise connection retries for safe methods (1–5) |
| `cache.enable` | In-memory response cache. Tuning keys (`cache.maxBytes`, `cache.maxEntryBytes`, `cache.exposeHeaders`, and others) are listed under [Response cache](/docs/configuration#response-cache) |
| `grpc.transcode.enabled` | Enable gRPC-JSON transcoding |
| `grpc.transcode.descriptorSet` | Path to FileDescriptorSet (`.pb`) |
| `grpc.transcode.services` | Comma-separated fully-qualified service names |
| `grpc.transcode.convertGrpcStatus` | Map non-OK `grpc-status` to HTTP + JSON body (default true) |
| `grpc.transcode.ignoreUnknownQueryParameters` | Ignore unknown query keys (default true) |
| `grpc.transcode.preserveProtoFieldNames` | Use proto field names in JSON (default false) |
| `grpc.transcode.alwaysPrintPrimitiveFields` | Always emit primitive defaults in JSON (default false) |
| `grpc.transcode.compression` | `true` or `gzip` to gzip framed payloads |

## Discovery: Consul and Kubernetes

Both remote modes keep serving the **last known good** destinations. A failed call, an unparseable body, or an empty result is never applied; the next successful non-empty answer replaces the set. Failures retry with exponential backoff and jitter (`discovery.backoffMinMs` to `discovery.backoffMaxMs`) and the delay resets after the next success.

**Consul** uses blocking queries: the first read is plain, then each call sends `?index=<X-Consul-Index>&wait=30s` and returns when the service changes or the wait elapses. An index that goes backwards restarts from a plain read. With `discovery.consulPassing` (default `true`) only instances whose checks all pass are used. A server that sends no index is polled every `discovery.intervalMs`.

```yaml
plus:
  options:
    discovery.mode: "consul"
    discovery.consulUrl: "http://consul:8500/v1/health/service/web"
    discovery.clusterId: "web"
```

**Kubernetes** lists EndpointSlices, then watches from the list `resourceVersion` with bookmarks enabled. Ready endpoints from every matching slice form the destination set. A `410 Gone` (or a watch `ERROR` event with code 410) drops the version and relists. In a pod the API server, namespace, service-account token, and CA come from `KUBERNETES_SERVICE_HOST` and `/var/run/secrets/kubernetes.io/serviceaccount`. The projected token is re-read on a timer and immediately after a 401 or 403, so rotation needs no restart. The service account needs `get`, `list`, and `watch` on `endpointslices` in `discovery.k8s.io`.

```yaml
plus:
  options:
    discovery.mode: "k8s"
    discovery.k8sService: "web"
    discovery.k8sNamespace: "prod"
    discovery.k8sPortName: "http"
    discovery.clusterId: "web"
```

## See also

- [CLI](/docs/cli)
- [Configuration](/docs/configuration)
- [Editions](/docs/editions)
- [gRPC-JSON transcoding](/docs/grpc-json-transcoding)
