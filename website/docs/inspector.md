# Inspector

Desktop MITM debugger (Avalonia). Licensed under [PolyForm Noncommercial](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt).

## Install

Windows:

- [MSI](https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.msi)
- [Portable zip](https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.zip)

```shell
winget install justcoding121.TitaniumInspector
```

## Quick use

1. Start interception from the **Capture** menu.
2. Install the root CA when prompted.
3. Toggle system proxy.

Default bind is typically `127.0.0.1:8866`.

## Features

- Session grid: method, status, host, URL, protocol, duration, TTFB, size, process
- Inspectors: Headers, Body, Hex, Frames
- Composer, breakpoints (edit body / continue / abort)
- AutoResponder and light scripts (`set-header` / `set-status` / `abort`)
- HAR import/export, archive, replay
- System proxy and root CA install / untrust / export / device setup
- Search (`method:GET status:200 host:example is:ws`)
- Optional Plus panels when `Titanium.Plus.dll` is present

## See also

- [Download](/download)
- [Editions](/docs/editions)
- [Library](/docs/library) for embedding the same engine
