using Titanium.Inspector.InstallSmoke;

// Phase 0 baselines (worktree fast-forwarded to origin/develop before this driver existed):
// SHA 9f36e1c4 fix(macos): harden Inspector CA trust and polish DMG install UX.
// dotnet 10.0.401. Inspector VersionPrefix 8.0.3, so the upgrade seed is the previous
// stable tag v8.0.2 (newest published release that is not this version).
// Assets used by this smoke:
//   TitaniumInspector-win-x64.msi, TitaniumInspector-linux-x64.deb,
//   TitaniumInspector-osx-arm64.dmg,
//   Titanium.Cli-win-x64.zip, Titanium.Cli-linux-x64.zip, Titanium.Cli-linux-x64.deb,
//   Titanium.Cli-osx-arm64.zip.
// v8.0.3 publishes the same file names. Real MSI install/upgrade is GitHub-only.
// On this PC, local-windows extracts with msiexec /a and restores the real profile.

var code = await SmokeApp.RunAsync(args);
Environment.ExitCode = code;
