# Phase 0 baseline artifacts

BASELINE_SHA: c14228d207413cf81e2b1eac73fb5a7b86cf634a
(develop tip when branch fix/plus-license-boundary was cut)

## Copyright holders

Caching, Xds, GeoIp, ClusterHealthProber, GrpcHealthProbe, and Plus:
- justcoding121 <justcoding121@users.noreply.github.com>
- Jehonathan Thomas <jehonathant@gmail.com>
Same person; PolyForm relicensing is OK.

## CLI publish listings

- cli-win-x64-files.txt: Release publish -r win-x64 --self-contained -p:PublishSingleFile=true
  (matches release.yml). Satellites are NOT present — CopyGeoIpSatellite runs AfterTargets=Build only.
- cli-framework-dependent-files.txt: framework-dependent publish (no RID).
- cli-build-output-files.txt: Release build output after CopyGeoIpSatellite
  (includes Titanium.Xds.dll, Titanium.Cli.GeoIp.dll, MaxMind.Db.dll).

## Plus payload gap (confirmed)

Clean framework-dependent CLI + only Titanium.Plus.dll (what update --plus installs today):
Plus activation failed: Could not load file or assembly 'StackExchange.Redis'...

Plus NuGet deps missing from the MIT CLI zip / single-DLL Plus install:
BCrypt.Net-Next, DnsClient, Google.Api.CommonProtos, Google.Protobuf,
Microsoft.IdentityModel.*, StackExchange.Redis, System.IdentityModel.Tokens.Jwt,
Pipelines.Sockets.Unofficial, (and BouncyCastle.Cryptography when present).

Phase 4 Plus bundle must include those managed assemblies (plus Xds, GeoIp, MaxMind.Db).

## Middleware order

Cache middleware is registered by RunCommand before Plus activates; Plus AccessSecurity /
WAF / rate-limit append after. Cache HIT returns without calling next, so CIDR/JWT/WAF
do not run on a hit. Characterization:
`HttpResponseCacheBehaviorTests.CacheHit_IsServedBeforeLaterMiddleware_PinnedCurrentOrder`.

Note: `ReloadConfigAsync` does not rebuild the middleware list or re-apply Plus options, so an
E2E that warms the cache then reloads a deny CIDR cannot observe the order change. Flagged for
a separate security review; do not change the order in this work.

## Cache purge when disabled

Covered by ControlPlane_CachePurge_WhenCacheDisabled_StillOk (E2E).
