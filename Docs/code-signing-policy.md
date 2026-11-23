# Code signing policy

**Signer: Azure Artifact Signing (Microsoft).** Release binaries are Authenticode-signed with a Public Trust certificate issued through Azure Artifact Signing (formerly Trusted Signing). Microsoft validates the publisher identity before it issues the certificate, so the publisher name Windows shows is a verified one.

**Status: configured when the owner completes the setup in `Docs/release-runbook.md` ("Code signing setup").** Until the repository variable `ARTIFACT_SIGNING_ACCOUNT` is set, the release workflow skips every signing step and ships UNSIGNED, and it says so with a build warning. Nothing below is in effect until then.

**SignPath Foundation application: pending.** We intend to move to free open-source code signing from the SignPath Foundation (https://signpath.org/) if the application is approved. Until then this document describes Azure Artifact Signing. Once SignPath is in use, this attribution line goes here:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Who builds

Every release binary is built by this repository's GitHub Actions workflow (`.github/workflows/release.yml`) from a `vMAJOR.MINOR.PATCH` tag on the public source in https://github.com/Awful-Waffle-Rofl/dreamweave-launcher. No exe built on a maintainer's PC is ever signed or released. The signing keys never leave Microsoft's service: the workflow only holds a short-lived token, obtained through OpenID Connect from a federated credential, and there is no certificate or secret stored in GitHub.

## Who approves

The repository owner (Awful-Waffle-Rofl) is committer, reviewer and release approver. Signing runs in the `package-signed` job, which uses the GitHub environment `signing`. That environment lists the owner as its required reviewer, so each signing request is approved manually by the owner after checking that the run came from the expected tag. The environment only accepts `v*.*.*` tags (and `main` for rehearsal runs), and the Azure federated credential trusts only that environment, so no other workflow, branch or fork can sign. When more maintainers join, they will be listed here with their roles.

## What is signed

Two passes, both with SHA-256 file digests and an RFC 3161 timestamp (`http://timestamp.acs.microsoft.com`, SHA-256), so signatures stay valid after the certificate expires:

1. Before the setup is built: `DreamWeaveLauncher.exe`, `Launcher.Core.dll` and `DreamWeaveDecalLaunch.exe` (the isolated x86 Decal launch helper), exactly those three files by name.
2. After the setup is built: the setup installer `DreamWeaveLauncher-Setup.exe`, the file players download.

`SHA256SUMS` is computed after the setup is signed, so it covers the signed file. A verify step (`tools/release/assert-signed.ps1`) fails the release unless all four files have a valid signature and a timestamp.

Not signed: third-party DLLs (`Newtonsoft.Json.dll`) are never signed by us, and the workflow does not sign or modify any other file. The uninstaller that Inno Setup embeds in the setup (`unins000.exe`, which the setup writes to the install folder) is also not signed. It is created on the player's own PC and never carries the downloaded-from-the-internet mark, so SmartScreen does not check it, and signing it would need Inno's SignTool hook with the Artifact Signing client library for no practical gain.

## Separate from manifest signing

The offline manifest key (see `Docs/release-runbook.md`) is a different thing: it signs the download manifest, is not a code-signing certificate, and is never used in CI.

## Privacy

See `Docs/privacy.md`.
