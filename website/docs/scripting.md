# Scripting

Write a route filter in Lua 5.4 or ship one as a WebAssembly module, and run it behind the same `middleware:` list as a .NET plugin. This page is for operators and plugin authors who want to edit or add a filter without building a .NET assembly.

**Status:** the full surface lands in subsequent commits. Today this page is a placeholder. The decision, the cost budgets, and the safety rules are on [Scripting engines](/docs/plugins#scripting-engines) in Per-route plugins.

## What will be here

- The `plugins:` fields for `kind: lua` and `kind: wasm`: `file`, `vmId`, `bodyMode`, `calloutHosts`, and `limits`.
- The script API: what a script reads from a request and which commands it can return.
- The per-engine limits and what happens to a request when one is hit.
- The native libraries, and how they ship for each platform.

Until then, use a [.NET plugin](/docs/plugins), the `extAuth` policy, or route transforms.
