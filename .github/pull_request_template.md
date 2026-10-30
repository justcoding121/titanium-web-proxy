## Summary

<!-- What changed and why. Name the behavior, not the tooling. -->

## Work items

<!-- List WI-n covered by this PR (one commit per work item on feat/commercial-grade-parity). -->

## Regression proof (per commit)

Tick for every work-item commit in this PR:

- [ ] Default cost is `none` or `one bool check` when the new knob is unset
- [ ] Terminate-lite stays eligible when unset; disabled only when set (tests present)
- [ ] `HotPathAllocationTests` ceilings unchanged (or a new ceiling is lower)
- [ ] No new lock, shared write, or allocation on the default request path
- [ ] Hot logic lives in a separate `NoInlining` method when it can be off
- [ ] New per-request fields cleared in `ResetForKeepAlive`; `KeepAliveResetCoversAllSessionFields` still passes
- [ ] Every new resource has an owner and a disposal/reload test
- [ ] Every new map/cache/session table has a size cap, eviction rule, and concurrency stress test
- [ ] Every external call has a timeout and honors the request `CancellationToken`
- [ ] Security features fail closed unless explicitly configured otherwise
- [ ] Schema / `PublicAPI.Unshipped.txt` / docs updated for this item
- [ ] No performance numbers claimed in this PR text (maintainers run paired A/B)

## Test plan

- [ ] `dotnet build src/Titanium.Web.Proxy.sln -c Release --warnaserror`
- [ ] Unit, Integration, Configuration, Cli, Plus, E2E projects green
- [ ] New tests failed first for the stated reason
- [ ] Maintainers: link `rps-ab.yml` run(s) per commit SHA (links only)

## Merge

Do **not** squash-merge. Preserve per-work-item commits (merge commit or rebase-merge).
