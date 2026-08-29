# Titanium.Web.Proxy.Configuration

MIT-licensed YAML/JSON configuration binding for Titanium Web Proxy (schema **7.1**).

Use this package when you want to load `twp.yaml` / `twp.json`, site-files, or reverse-proxy document dialects into Abstractions route/cluster models and the optional `server:` section (profiles, timeouts, TLS, limits, upstream, …). Embedders that only set `ForwardHost` do not need this package. Plus feature options stay under `plus:` — they do not configure `ProxyServer`.
