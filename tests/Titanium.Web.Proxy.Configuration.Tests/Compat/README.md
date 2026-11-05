# Config compatibility corpus

Each file here is a **golden** configuration in the shape of an older release. Every one of them must keep loading
through `TwpConfigLoader` and validating without errors (`ConfigCompatibilityTests`). The test enumerates this
directory, so adding a file adds a case.

## Schema bump rules

`schemaVersion` is informational: the loader does not branch on it, so compatibility is kept by how the model
evolves, not by version switches.

1. **Additive by default.** A new key must be optional, and leaving it out must keep the previous behaviour. New
   sections default to `null` or an empty list.
2. **Never rename, retype or remove** a key that shipped in a release. Rename by adding the new key and keeping the
   old one as an alias until the next major version, then document the removal in the migration guide.
3. **Unknown keys are ignored** by both the YAML and JSON readers, so a file written for a newer minor version still
   loads on an older one. Do not turn that off.
4. **Tighten validation only behind a flag.** A rule that rejects a file that used to load needs an explicit opt-in,
   or a major version bump with a migration note.
5. **Minor bump (7.x to 7.y)** for additive keys. **Major bump (7 to 8)** only for a breaking change, together with a
   golden file for the last shape of the old major and a migration page.
6. When a release ships, copy a representative file of its shape here as `v<schemaVersion>-<topic>.yaml|json`. Never
   edit an existing golden file to make a test pass; fix the schema change instead.
