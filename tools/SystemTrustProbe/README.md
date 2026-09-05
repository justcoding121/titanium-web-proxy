# SystemTrustProbe

Internal diagnostic for macOS/Linux **machine** (System.keychain) CA trust install/remove and MITM checks.

```bash
dotnet run --project tools/SystemTrustProbe -- status
dotnet run --project tools/SystemTrustProbe -- install-system   # admin password
dotnet run --project tools/SystemTrustProbe -- run              # decrypt proxy + optional system proxy
dotnet run --project tools/SystemTrustProbe -- remove-system
dotnet run --project tools/SystemTrustProbe -- curl-check
```

Not shipped with the NuGet package. Prefer login-keychain trust for interactive apps; use this to validate `TrustRootCertificateAsAdmin(machineTrusted: true)` / System.keychain behavior.
