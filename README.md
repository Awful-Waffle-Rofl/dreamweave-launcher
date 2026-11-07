# DreamWeave Launcher

A single small Windows executable that installs, launches and uninstalls the
client setup for the DreamWeave Asheron's Call emulator server.

**Not affiliated with WB Games or Turbine.** This is a community project.

## What it is

One exe (target size around 1.5 MB) that acts as installer, launcher and
uninstaller. The player ticks checkboxes for the pieces they need, the launcher
downloads them, and then it starts `acclient.exe` with login arguments for
DreamWeave.

## Legal posture, in plain words

- It ships only our own code. It never bundles, caches or re-hosts any WB Games
  or Turbine files.
- The client installer is downloaded by the player from archive.org, and the
  End-of-Retail data from an existing community mega.nz link. Every download is
  verified by SHA-256 against a signed manifest that we host.
- It launches `acclient.exe` with login arguments for DreamWeave only.
- By default it never modifies the client, never injects into client files or
  client processes, and never requires a modified client to play.
- It offers exactly one optional fix, which is off by default: the community
  palette memory-leak fix from
  [eriknihlen/ac-eor-palette-leak-fix](https://github.com/eriknihlen/ac-eor-palette-leak-fix)
  (MIT). It replaces 6 bytes in `acclient.exe` with NOPs (two 3-byte
  instructions). It is applied only if the player ticks it. The launcher checks
  the file's SHA-256 before and after the change and keeps the original so the
  fix can be reverted. The patched file is produced on the player's own machine
  and is never distributed.
- It has an uninstaller. Telemetry, if any is ever added, will be opt-in with a
  published privacy policy.
- Everything in this repository is under the MIT license. Code signing is
  planned through SignPath Foundation, with public CI builds.

No Asheron's Call logos or artwork are used in this repository.

## Repository layout

- `src/Launcher.Core` - class library shared by the launcher and the tools.
- `src/Launcher` - the WinForms executable (`DreamWeaveLauncher.exe`). Referenced
  assemblies are embedded with Costura.Fody.
- `tools/ManifestTool` - console tool for generating keys and signing and
  verifying the download manifest (`keygen`, `sign`, `verify`).
- `manifest/manifest.sample.json` - a sample schema-1 manifest with the real
  download hashes. See `Docs/manifest-compat.md` for the rules for changing the
  manifest.
- `tests/Launcher.Tests` - MSTest unit tests.

All projects target `net48`. The `Microsoft.NETFramework.ReferenceAssemblies`
package is referenced from `Directory.Build.props`, so the .NET Framework 4.8
Developer Pack is not required to build.

## Build and test

```
dotnet build DreamWeaveLauncher.sln -c Release
dotnet test DreamWeaveLauncher.sln -c Release
```

CI builds on Windows and fails if the launcher exe grows past 3 MB.

## License

MIT, see `LICENSE`. Third-party components are listed in
`THIRD-PARTY-NOTICES.md`.

## Credits

- The optional palette leak fix (its offsets, original bytes and before/after
  hashes) comes from
  [eriknihlen/ac-eor-palette-leak-fix](https://github.com/eriknihlen/ac-eor-palette-leak-fix),
  MIT licensed.
