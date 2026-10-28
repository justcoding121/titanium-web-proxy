# DNS / CloudFront helpers for titaniumproxy.com

Public hostnames only — **no AWS credentials or certificate private keys**.

Live edge (maintainer machine, profile `jthomas`):

1. ACM cert in `us-east-1` for `titaniumproxy.com` + `www` (DNS validation into zone `Z08516571F59U4HNOJXAT`)
2. CloudFront distribution aliases those names; origin `justcoding121.github.io` (HTTPS)
3. CloudFront Function [`cloudfront-path-rewrite.js`](cloudfront-path-rewrite.js) (`titaniumproxy-path-rewrite`) prepends `/titanium-web-proxy` so project Pages works with VitePress `base: '/'`. Do **not** attach an origin-request policy that forwards the viewer `Host` — GitHub Pages returns “Site not found” unless the origin Host is `justcoding121.github.io`.
4. Route53 A/AAAA aliases → CloudFront ([`route53-alias.json`](route53-alias.json))
5. Cache policy `titaniumproxy-origin-ttl-no-default` ([`cache-policy-origin-ttl.json`](cache-policy-origin-ttl.json)). GitHub Pages sends `max-age=600` on files and no `Cache-Control` on 404s. The AWS managed CachingOptimized policy stores a headerless 404 for 24 hours, so a stylesheet that misses during a publish stays missing and the page renders unstyled. This policy honors origin `max-age` and does not store responses that omit it. Deploys also keep hashed `/assets` from recent publishes so cached HTML can still load its CSS.

Template: [`cloudfront-distribution.json`](cloudfront-distribution.json) (substitute ACM ARN; function ARN uses your account).
