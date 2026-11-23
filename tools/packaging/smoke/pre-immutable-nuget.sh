#!/usr/bin/env bash
# Pack the three MIT packages, restore them from a local feed, and start a proxy.
# The packages are prerelease 8.0.7-local so a public 8.0.7 cannot satisfy the restore.
set -euo pipefail
root="$(cd "$(dirname "$0")/../../.." && pwd)"
work="$(mktemp -d)"
feed="$work/feed"
mkdir -p "$feed"
for proj in \
  src/Titanium.Web.Proxy.Abstractions/Titanium.Web.Proxy.Abstractions.csproj \
  src/Titanium.Web.Proxy.Configuration/Titanium.Web.Proxy.Configuration.csproj \
  src/Titanium.Web.Proxy/Titanium.Web.Proxy.csproj
do
  dotnet pack "$root/$proj" -c Release -p:Version=8.0.7-local -o "$feed" --nologo
done
count="$(find "$feed" -name '*.nupkg' ! -name '*.symbols.nupkg' | wc -l | tr -d ' ')"
if [[ "$count" -lt 3 ]]; then
  echo "ASSERT: expected 3 MIT packages, found $count" >&2
  exit 1
fi
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
cat > "$work/smoke.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Titanium.Web.Proxy" Version="8.0.7-local" />
  </ItemGroup>
</Project>
EOF
cat > "$work/Program.cs" <<'EOF'
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

using var proxy = new ProxyServer(false, false, false);
proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
proxy.Start();
var port = proxy.ProxyEndPoints[0].Port;
Console.WriteLine($"listening {port}");
if (port <= 0)
{
    return 1;
}

proxy.Stop();
return 0;
EOF
dotnet run --project "$work/smoke.csproj" --nologo
echo "nuget local feed ok"
