# Code signing policy

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

**Status: application pending; releases are unsigned until approved.** DreamWeave Launcher uses the free code signing program of the SignPath Foundation (https://signpath.org/) for open-source projects. Until the SignPath Foundation approves the project and the maintainer finishes the setup in `Docs/release-runbook.md` ("Code signing setup"), the release workflow skips every signing step, ships UNSIGNED and says so with a build warning. Nothing below is in effect until then. The attribution line above applies from the first signed release.

This is the project's code signing policy. It is linked from the project home page (`README.md`) and from every release page.

## Who builds

Every release binary is built by this repository's GitHub Actions workflow (`.github/workflows/release.yml`) from a `vMAJOR.MINOR.PATCH` tag on the public source in https://github.com/Awful-Waffle-Rofl/dreamweave-launcher. The tag must point at a commit already on `main`; a `verify-tag` job checks that before any signing request is made. No file built on a maintainer's PC is ever signed or released. The signing key never leaves SignPath's service; the workflow only holds an API token that can submit signing requests for this project.

## Team roles

| Role | Member |
|---|---|
| Authors (committers) | Awful-Waffle-Rofl |
| Reviewers | Awful-Waffle-Rofl |
| Approvers | Awful-Waffle-Rofl |

Every member needs multi-factor authentication on both GitHub and SignPath. When more maintainers join, they are listed here with their roles.

## Who approves

Every release is approved manually in SignPath by an approver, after checking that the signing request came from the expected tag and the public build. The workflow waits for that approval, once for the binaries and once for the setup. SignPath records the build's origin (repository, commit, workflow run) from GitHub, not from the build script.

## What is signed

Two passes, each submitted to SignPath from the release workflow:

1. Before the setup is built: `DreamWeaveLauncher.exe`, `Launcher.Core.dll` and `DreamWeaveDecalLaunch.exe` (the isolated x86 Decal launch helper), exactly those three files by name.
2. After the setup is built: the setup installer `DreamWeaveLauncher-Setup.exe`, the file players download.

`SHA256SUMS` is computed after the setup is signed, so it covers the signed file. A verify step (`tools/release/assert-signed.ps1`) fails the release unless all four files have a valid, timestamped signature.

Not signed: third-party DLLs (`Newtonsoft.Json.dll`) are never signed by us, and the workflow does not sign or modify any other file. The uninstaller that Inno Setup embeds in the setup (`unins000.exe`, which the setup writes to the install folder) is also not signed. It is created on the player's own PC and never carries the downloaded-from-the-internet mark, so SmartScreen does not check it.

The setup provides uninstallation (a normal Apps and features entry), and every signed file carries the product name "DreamWeave Launcher" and the same version number.

## Separate from manifest signing

The offline manifest key (see `Docs/release-runbook.md`) is a different thing: it signs the download manifest, is not a code-signing certificate, and is never used in CI.

## Privacy

See `Docs/privacy.md`.
