# Code signing policy

**Status: PENDING APPROVAL. This is a placeholder.** DreamWeave Launcher v1 ships UNSIGNED. We intend to apply to the SignPath Foundation (https://signpath.org/) for free code signing for open-source projects. Nothing below is in effect until that application is approved.

Once approved, this line goes here:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Who builds

Every release exe is built by this repository's GitHub Actions workflow (`.github/workflows/release.yml`) from a `vMAJOR.MINOR.PATCH` tag on the public source in https://github.com/Awful-Waffle-Rofl/dreamweave-launcher. No exe built on a maintainer's PC is ever signed or released.

## Who approves

Roles are to be confirmed in the SignPath application. Planned: the repository owner (Awful-Waffle-Rofl) is committer, reviewer and release approver. Each signing request is approved manually by the owner after checking that the build came from the expected tag. When more maintainers join, they will be listed here with their roles.

## What is signed

Only `DreamWeaveLauncher.exe` produced by the release workflow. The launcher does not sign or modify any other file, and it never signs third-party binaries.

## Separate from manifest signing

The offline manifest key (see `Docs/release-runbook.md`) is a different thing: it signs the download manifest, is not a code-signing certificate, and is never used in CI.

## Privacy

See `Docs/privacy.md`.
