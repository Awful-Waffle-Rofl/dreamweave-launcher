# DreamWeave Launcher

A small Windows launcher that installs, launches and cleans up the client setup
for the DreamWeave Asheron's Call emulator server. It comes in a normal per-user
Windows setup program (no administrator rights needed).

**Not affiliated with WB Games or Turbine.** This is a community project.

## What it is

The player runs `DreamWeaveLauncher-Setup-<version>.exe` from the GitHub Releases
page. It installs the launcher (one exe and two DLLs) under `%LOCALAPPDATA%\Programs`,
adds a Start menu shortcut, and is removed from Windows Settings > Apps like any
other program; uninstalling also removes saved passwords and launcher data. In
the launcher the player ticks checkboxes for the pieces they need, the launcher
downloads them, and then it starts `acclient.exe` with login arguments for
DreamWeave.

## Legal posture, in plain words

- It ships only our own code. It never bundles, caches or re-hosts any WB Games
  or Turbine files.
- The player downloads the original game installer and the final game data
  files from community uploads on mega.nz. The installer also has a backup copy
  on the Internet Archive. Every download is verified by SHA-256 against a
  signed manifest that we host.
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
- It installs and uninstalls through a normal Inno Setup program. Telemetry, if any is ever added, will be opt-in with a
  published privacy policy.
- Everything in this repository is under the MIT license. Code signing is
  planned through SignPath Foundation, with public CI builds.

No Asheron's Call logos or artwork are used in this repository.

## Repository layout

- `src/Launcher.Core` - class library shared by the launcher and the tools.
- `installer` - the Inno Setup 6 script for the per-user setup program, and the
  privacy text it shows (kept in sync with `Docs/privacy.md` by a test).
- `src/Launcher` - the WinForms executable (`DreamWeaveLauncher.exe`). It ships with
  its dependency DLLs (`Launcher.Core.dll`, `Newtonsoft.Json.dll`) as ordinary files in the same folder; nothing is packed into the exe.
  `installer/shipped-files.txt` is the one list of shipped files.
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

CI builds on Windows, checks the build output against `installer/shipped-files.txt` (exe under 3 MB, shipped files under 4 MiB in total, no other files), and builds the
setup program with Inno Setup 6 (`tools/release/build-setup.ps1`; install Inno
Setup 6 locally to run it).

## Docs

- [`Docs/release-runbook.md`](Docs/release-runbook.md) - maintainer release procedure, key setup and rotation.
- [`Docs/code-signing-policy.md`](Docs/code-signing-policy.md) - code signing policy (pending approval).
- [`Docs/privacy.md`](Docs/privacy.md) - privacy statement.

## License

MIT, see `LICENSE`. Third-party components are listed in
`THIRD-PARTY-NOTICES.md`.

## Credits

- The optional palette leak fix (its offsets, original bytes and before/after
  hashes) comes from
  [eriknihlen/ac-eor-palette-leak-fix](https://github.com/eriknihlen/ac-eor-palette-leak-fix),
  MIT licensed.
