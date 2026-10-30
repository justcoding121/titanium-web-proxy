# Lua header gate

`header_gate.lua` does what the .NET sample in `../Titanium.Web.Proxy.Examples.RoutePlugin` does, without building an assembly: a request that lacks one header gets `403`, and a request that has it goes on with one more request header.

| | .NET (`HeaderGatePlugin.cs`) | Lua (`header_gate.lua`) |
| --- | --- | --- |
| Required header | `settings.requiredHeader`, default `X-Api-Client` | the same |
| Stamp header | `settings.stampHeader`, default `X-Route-Plugin`, value `header-gate` | the same |
| Missing header | `403`, body `missing X-Api-Client` | the same |

## Use

1. Check that the scripting natives load: `titanium scripting-deps status`. The release zips bundle `lua54.dll` / `liblua54` beside the binary.
2. Put `header_gate.lua` next to your `twp.yaml` and merge [`twp.fragment.yaml`](twp.fragment.yaml) into it.
3. Run `titanium run -c twp.yaml` from the directory that holds the config (the check that `file:` exists runs against the current directory), then:

```powershell
curl.exe -i http://127.0.0.1:8080/api                                  # 403, missing X-Api-Client
curl.exe -i -H "X-Api-Client: demo" http://127.0.0.1:8080/api          # the backend sees X-Route-Plugin: header-gate
```

## Where it runs

A plugin listed in a route's `middleware:` runs `on_request_headers` for that route only. Listed in the top-level `scriptPipeline:` it runs on every request of every route, and the script's other phases (`on_response_headers`, `on_response_body`, `on_log`) run too. Do not list the same plugin in both places, or its request-headers phase runs twice.

## Limits

The script runs in a Lua 5.4 sandbox with a memory cap and an instruction cap per call. `failMode: closed` answers `503` when it fails or times out. See the scripting guide in `website/docs/scripting.md`, and `../scripting` for more recipes.
