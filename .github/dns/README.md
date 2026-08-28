# DNS / CloudFront helpers for titaniumproxy.com

Public hostnames only — **no AWS credentials or certificate private keys**.

Live edge (maintainer machine, profile `jthomas`):

1. ACM cert in `us-east-1` for `titaniumproxy.com` + `www` (DNS validation into zone `Z08516571F59U4HNOJXAT`)
2. CloudFront distribution aliases those names; origin `justcoding121.github.io` (HTTPS)
3. CloudFront Function [`cloudfront-path-rewrite.js`](cloudfront-path-rewrite.js) (`titaniumproxy-path-rewrite`) prepends `/titanium-web-proxy` so project Pages works with VitePress `base: '/'`
4. Route53 A/AAAA aliases → CloudFront ([`route53-alias.json`](route53-alias.json))

Template: [`cloudfront-distribution.json`](cloudfront-distribution.json) (substitute ACM ARN; function ARN uses your account).
