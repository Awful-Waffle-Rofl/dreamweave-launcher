# Release runbook (maintainer)

The launcher is built by CI and shipped to players as a per-user Inno Setup 6 installer (`DreamWeaveLauncher-Setup.exe`, a fixed name with no version, built from `installer/DreamWeaveLauncher.iss`). The download manifest is signed by hand, offline. The two are published separately: the setup as a GitHub Release, the manifest as two files on the `manifest` branch.

To check the installer without a release: every PR and push to `main` runs `build.yml`, which uploads the `DreamWeaveLauncher-Setup` artifact. To build it locally, install Inno Setup 6.6 or newer (`WizardStyle=modern dark` needs 6.6) and run `./tools/release/build-setup.ps1 -ExePath <DreamWeaveLauncher.exe> -OutDir <dir>` after a Release build; the script fails with a clear message if Inno Setup 6 is missing.

Commands below use `dotnet run --project tools/ManifestTool -c Release --` as `ManifestTool`. Run them from a checkout of `main` at the release tag.

## Normal release

1. **Bump the version.** Edit all three together: `<Version>` in `Directory.Build.props` (for example `1.0.1`), the `assemblyIdentity` version in `src/Launcher/app.manifest` (`1.0.1.0`), and the `launcher.version` in `manifest/manifest.sample.json` (`1.0.1`). Open a PR, merge it.
2. **Tag and push.** On the merge commit: `git tag v1.0.1` then `git push origin v1.0.1`. The tag must be exactly `v` + `<Version>`; the workflow `.github/workflows/release.yml` fails otherwise. It builds, runs the tests, checks the build output against `installer/shipped-files.txt` (file list and size caps), builds the setup, asserts the tag equals the exe's assembly version and the setup's product version, and creates a DRAFT release with `DreamWeaveLauncher-Setup.exe` and `SHA256SUMS`. The tag's commit must already be on `main`: a `verify-tag` job checks that first, before any signing approval prompt can appear. When SignPath is configured, the workflow submits the binaries and the setup for signing and WAITS for your approval in SignPath (two requests); without it the release is unsigned. The bare exe is not a release asset. To rehearse without a release, dispatch the workflow by hand with a tag input.
3. **Download and verify.** From the DRAFT release download both files. In PowerShell: `Get-FileHash .\DreamWeaveLauncher-Setup.exe -Algorithm SHA256` must equal the (case-insensitive) hash in `SHA256SUMS`.
4. **Publish the release.** In GitHub, open the draft and publish it. A draft's asset URLs return 404 to everyone, so the release must be public before any manifest points at it. Confirm the download URL `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest/download/DreamWeaveLauncher-Setup.exe` now downloads the new setup (the per-tag URL `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/download/vX.Y.Z/DreamWeaveLauncher-Setup.exe` still works). The website links to the `latest/download` URL, so the asset name must never change. Players install by running that setup. The setup is one click: no wizard pages (every `Disable*Page` directive is `yes`, there is no `[Tasks]` section and no info file, and a `CurPageChanged` hook auto-clicks Next on the Ready to Install page, which may flash briefly, because Inno ignores `DisableReadyPage` when no earlier page was shown and a `ShouldSkipPage` hook did not remove it), a short progress window, and then the launcher starts by itself unless the setup was run with `/SILENT` or `/VERYSILENT`. Both shortcuts (Start menu and desktop) are always created. An upgrade reuses the previous folder, and `/DIR=` still works but is refused under `%LOCALAPPDATA%\DreamWeave`.
5. **Print the launcher block.** `./tools/release/print-manifest-launcher-block.ps1 -Version 1.0.1`. It prints the `version` line only (add `-DownloadPage <https url>` to print `downloadPage` too).
6. **Edit `manifest.json`.** In a worktree of the `manifest` branch (see "Creating the manifest branch"), replace only the `version` line of the `launcher` object with the printed one. Keep `minVersion` and `downloadPage` (the page players open to get the new setup, normally `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest`), and delete any leftover `url`, `sha256`, `size` or `authenticodeSubject` lines. Increment `serial` by at least 1 and update `issued`. Edit with an editor that keeps the file's bytes sane (no BOM, no reformat on save).
7. **Sign, offline.** On the maintainer's PC only: `ManifestTool sign --key <path to private key> --manifest <path to manifest.json>`. This writes `manifest.json.sig` next to it.
8. **Verify.** `ManifestTool verify --pub <keyId:X:Y or public key file> --manifest <path to manifest.json>` must print `OK` with the serial you expect.
9. **Push the manifest.** Commit `manifest.json` and `manifest.json.sig` together to the `manifest` branch and push it. Confirm both raw URLs serve the new bytes (CDN caching can lag a few minutes; the launcher retries once with a cache-bust).

Releases are Authenticode-signed through SignPath once it is configured (see "Code signing setup" below and `Docs/code-signing-policy.md`). When it is not configured the release is unsigned and the workflow prints an UNSIGNED warning. When it is configured, approve each of the two signing requests in SignPath after checking the tag.

Launchers built before `downloadPage` existed ignore it; the only way to reach them is to raise `minVersion` (they then show the "download again from the website" banner). Never publish a build of the current code as a bare exe at `launcher.url`: an old launcher would swap it in and start it with `--updated`, which the current launcher rejects and exits silently. See `Docs/manifest-compat.md`.

## Creating the manifest branch (once)

The `manifest` branch is an orphan branch holding two signed files (`manifest.json` and `manifest.json.sig`) plus `.gitattributes` and `README.md`. Its starting files live in `manifest/branch-template/` on `main`.

```
git worktree add --detach ../dreamweave-launcher-manifest origin/main
cd ../dreamweave-launcher-manifest
git checkout --orphan manifest
git rm -rf .
git checkout origin/main -- manifest/branch-template
git mv manifest/branch-template/.gitattributes .gitattributes
git mv manifest/branch-template/README.md README.md
git add .gitattributes README.md
git commit -m "Initialize manifest branch"
git push -u origin manifest
```

Then copy `manifest/manifest.sample.json` from `main` to `manifest.json`, fill in the `launcher` block and `serial` as in the normal release, and sign and verify (steps 5 to 9, after the release is published). Check that `git ls-files` shows exactly the two signed files plus `.gitattributes` and `README.md`.

## First-time key setup

Do this before the first real release. Until a key is in `TrustedKeys.Production` every manifest is rejected (fail closed).

1. On the maintainer's PC: `ManifestTool keygen --out D:\keys\dw-manifest-key.json`. `--out` must be an absolute path OUTSIDE any git checkout (the example is on a separate drive); `.gitignore` also blocks `*manifest-key*.json`, `*.pem` and `*.key` as a backstop only. It prints the `keyId` and a `TrustedKey.Parse("keyId:X:Y")` line. It refuses to overwrite an existing file.
2. Store the private key file OFFLINE (encrypted USB stick or similar), plus a second backup copy in a different physical place. Never put it in the repository, in CI secrets, in cloud sync folders or in chat.
3. Add the printed `TrustedKey.Parse(...)` line to `TrustedKeys.Production` in `src/Launcher.Core/Manifests/TrustedKeys.cs` through a normal PR, and release a launcher containing it (normal release above). Players on older launchers cannot verify manifests until they have a build with the key.

## Key rotation

Use this when the key may be compromised or on a schedule. Keep a second keypair generated in advance as the backup key (run `keygen` for it at setup time and store it like the first).

1. Ship a launcher release whose `TrustedKeys.Production` lists BOTH the current key and the backup (new) key. Wait until players have updated.
2. Sign manifests with the new key from then on. Keep the outgoing key listed until every manifest signed by it has been re-signed.
3. In a later launcher release remove the retired key. Raise `MinimumSerial` in `TrustedKeys.cs` if old validly-signed manifests must stop being accepted.
4. Generate a fresh backup key and store it offline.

If the signing key is lost with no backup, no manifest can be accepted by existing launchers, and a new launcher build with a new key must be distributed by hand.

## Code signing setup (once)

CI signs through the SignPath Foundation's free open-source program (https://signpath.org/). Everything here is the owner's one-time work; until step 8 is done the workflow ships unsigned exactly as before. SignPath's own GitHub guide (https://docs.signpath.io/trusted-build-systems/github) is the authority for steps 4 and 5; the names below are the ones this workflow reads.

1. **Apply.** Apply to the SignPath Foundation (https://signpath.org/) for this project and wait for approval. Their terms require, among other things, a code signing policy reachable from the project home page and the release pages (`Docs/code-signing-policy.md`, linked from `README.md` and from the draft release notes), the attribution line in that policy, manual approval of every release, and a setup that provides uninstallation. Keep the policy's team table accurate.
2. **MFA.** Turn on multi-factor authentication for every team member on GitHub and on SignPath.
3. **Create the SignPath project.** After approval, in SignPath create the project (its slug becomes `SIGNPATH_PROJECT_SLUG`), a signing policy for release signing (slug `SIGNPATH_SIGNING_POLICY_SLUG`) that requires manual approval, and TWO artifact configurations. Both have a `<zip-file>` root, because `actions/upload-artifact` zips what it uploads, and sign Authenticode (PE) files at the zip root:
   - one for the three binaries only: `DreamWeaveLauncher.exe`, `Launcher.Core.dll`, `DreamWeaveDecalLaunch.exe` (slug `SIGNPATH_ARTIFACT_CONFIG_BINARIES`);
   - one for the setup: `DreamWeaveLauncher-Setup.exe` (slug `SIGNPATH_ARTIFACT_CONFIG_SETUP`).
   Note the organization ID too (`SIGNPATH_ORGANIZATION_ID`).
4. **GitHub integration.** Follow SignPath's GitHub guide: add the predefined trusted build system "GitHub.com" to the SignPath organization and link it to the project, and install the SignPath GitHub App on this repository.
5. **API token.** In SignPath create an API token for a user who has submitter permission on this project and signing policy.
6. **GitHub environment.** In the repository, Settings, Environments, create `signing`. Add the API token as an ENVIRONMENT secret named `SIGNPATH_API_TOKEN` (not a repository secret), so only the `package-signed` job can read it. Under Deployment branches and tags choose Selected branches and tags and add the tag rule `v*.*.*` (real releases) and the branch rule `main` (so a workflow_dispatch rehearsal from `main` is allowed; a dispatch runs on the branch ref). Required reviewers are optional here, because SignPath itself enforces the manual approval; if you add yourself, leave "Prevent self-review" OFF or you cannot approve your own run.
7. **Repository variables.** Settings, Secrets and variables, Actions, Variables tab: add `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_ARTIFACT_CONFIG_BINARIES` and `SIGNPATH_ARTIFACT_CONFIG_SETUP`. None is secret. Setting `SIGNPATH_ORGANIZATION_ID` is what switches signing on; deleting it switches it off again.
8. **Rehearse.** Actions, release, Run workflow from `main` with an existing tag name such as `v1.0.0`. The `package-signed` job submits the binaries and waits; approve the request in SignPath (a link to it is in the step log), then approve the setup request the same way. Each wait is capped at 3600 seconds. When the run finishes, open the "Assert signatures" step: it must list all four files as Valid and timestamped. Download the `release-assets` artifact and check the setup's Properties, Digital Signatures tab: the signer must read SignPath Foundation.

What this does not cover: the uninstaller that Inno Setup embeds in the setup stays unsigned. It is written on the player's PC without the downloaded-from-the-internet mark, so SmartScreen does not check it. See `Docs/code-signing-policy.md`.

If signing fails or an approval times out in a real release run, no draft release is created (the failure blocks it). Fix the cause and re-run the failed jobs. Do not delete `SIGNPATH_ORGANIZATION_ID` just to get a release out unless you intend to ship unsigned. Before publishing the draft, keep the "Code signing policy" link in the release notes.

## Never

- Never commit a private key (`dw-manifest-key.json` or any `keygen` output) anywhere.
- Never sign the manifest in CI, and never store the manifest private key or its contents as a CI secret. (Code signing in CI goes through SignPath, which keeps the signing key; CI holds only a submit-only API token. See "Code signing setup".)
- Never lower `serial`. Launchers reject any manifest with a serial below the highest they have seen, and below `MinimumSerial`.
- Never edit `manifest.json` or `manifest.json.sig` in GitHub's web editor, or edit `manifest.json` without re-signing.
- Never push a manifest whose launcher url is not yet downloadable.
