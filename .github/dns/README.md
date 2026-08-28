# DNS / CloudFront helpers for titaniumproxy.com

Public hostnames and GitHub Pages IPs/aliases only — **no AWS credentials or certificate private keys**.

Apply from a maintainer machine:

```bash
# 1) Request ACM cert (us-east-1) — see plan / maintainer runbook
# 2) Add ACM validation CNAMEs to hosted zone Z08516571F59U4HNOJXAT
# 3) Create CloudFront distribution from cloudfront-distribution.json (substitute CertificateArn)
# 4) Alias apex + www to the distribution domain (route53-alias.json template)
```

Profile: `jthomas`. Hosted zone: `Z08516571F59U4HNOJXAT`. CloudFront alias hosted zone id: `Z2FDTNDATAQYW2`.
