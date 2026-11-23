# Release runbook (maintainer)

The launcher is built by CI and shipped to players as a per-user Inno Setup 6 installer (`DreamWeaveLauncher-Setup.exe`, a fixed name with no version, built from `installer/DreamWeaveLauncher.iss`). The download manifest is signed by hand, offline. The two are published separately: the setup as a GitHub Release, the manifest as two files on the `manifest` branch.

To check the installer without a release: every PR and push to `main` runs `build.yml`, which uploads the `DreamWeaveLauncher-Setup` artifact. To build it locally, install Inno Setup 6.6 or newer (`WizardStyle=modern dark` needs 6.6) and run `./tools/release/build-setup.ps1 -ExePath <DreamWeaveLauncher.exe> -OutDir <dir>` after a Release build; the script fails with a clear message if Inno Setup 6 is missing.

Commands below use `dotnet run --project tools/ManifestTool -c Release --` as `ManifestTool`. Run them from a checkout of `main` at the release tag.

## Normal release

1. **Bump the version.** Edit all three together: `<Version>` in `Directory.Build.props` (for example `1.0.1`), the `assemblyIdentity` version in `src/Launcher/app.manifest` (`1.0.1.0`), and the `launcher.version` in `manifest/manifest.sample.json` (`1.0.1`). Open a PR, merge it.
2. **Tag and push.** On the merge commit: `git tag v1.0.1` then `git push origin v1.0.1`. The tag must be exactly `v` + `<Version>`; the workflow `.github/workflows/release.yml` fails otherwise. It builds, runs the tests, checks the build output against `installer/shipped-files.txt` (file list and size caps), builds the setup, asserts the tag equals the exe's assembly version and the setup's product version, and creates a DRAFT release with `DreamWeaveLauncher-Setup.exe` and `SHA256SUMS`. The bare exe is not a release asset. To rehearse without a release, dispatch the workflow by hand with a tag input.
3. **Download and verify.** From the DRAFT release download both files. In PowerShell: `Get-FileHash .\DreamWeaveLauncher-Setup.exe -Algorithm SHA256` must equal the (case-insensitive) hash in `SHA256SUMS`.
4. **Publish the release.** In GitHub, open the draft and publish it. A draft's asset URLs return 404 to everyone, so the release must be public before any manifest points at it. Confirm the download URL `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest/download/DreamWeaveLauncher-Setup.exe` now downloads the new setup (the per-tag URL `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/download/vX.Y.Z/DreamWeaveLauncher-Setup.exe` still works). The website links to the `latest/download` URL, so the asset name must never change. Players install by running that setup. The setup is one click: no wizard pages (every `Disable*Page` directive is `yes`, there is no `[Tasks]` section and no info file, and a `CurPageChanged` hook auto-clicks Next on the Ready to Install page, which may flash briefly, because Inno ignores `DisableReadyPage` when no earlier page was shown and a `ShouldSkipPage` hook did not remove it), a short progress window, and then the launcher starts by itself unless the setup was run with `/SILENT` or `/VERYSILENT`. Both shortcuts (Start menu and desktop) are always created. An upgrade reuses the previous folder, and `/DIR=` still works but is refused under `%LOCALAPPDATA%\DreamWeave`.
5. **Print the launcher block.** `./tools/release/print-manifest-launcher-block.ps1 -Version 1.0.1`. It prints the `version` line only (add `-DownloadPage <https url>` to print `downloadPage` too).
6. **Edit `manifest.json`.** In a worktree of the `manifest` branch (see "Creating the manifest branch"), replace only the `version` line of the `launcher` object with the printed one. Keep `minVersion` and `downloadPage` (the page players open to get the new setup, normally `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest`), and delete any leftover `url`, `sha256`, `size` or `authenticodeSubject` lines. Increment `serial` by at least 1 and update `issued`. Edit with an editor that keeps the file's bytes sane (no BOM, no reformat on save).
7. **Sign, offline.** On the maintainer's PC only: `ManifestTool sign --key <path to private key> --manifest <path to manifest.json>`. This writes `manifest.json.sig` next to it.
8. **Verify.** `ManifestTool verify --pub <keyId:X:Y or public key file> --manifest <path to manifest.json>` must print `OK` with the serial you expect.
9. **Push the manifest.** Commit `manifest.json` and `manifest.json.sig` together to the `manifest` branch and push it. Confirm both raw URLs serve the new bytes (CDN caching can lag a few minutes; the launcher retries once with a cache-bust).

Releases are Authenticode-signed by CI once Artifact Signing is configured (see "Code signing setup" below and `Docs/code-signing-policy.md`). When it is not configured the release is unsigned and the workflow prints an UNSIGNED warning. When it is configured, the signing job waits for your approval in the `signing` environment: approve it in the Actions run after checking the tag.

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

CI signs with Azure Artifact Signing. Everything here is the owner's one-time work; until step 7 is done the workflow ships unsigned exactly as before. Nothing here is a secret: the six values go in repository VARIABLES, not secrets.

1. **Create the account.** In the Azure portal create an Artifact Signing account (Basic tier) in a region that supports it, and note the account name and the region's endpoint URI (for example `https://eus.codesigning.azure.net/` for East US). The endpoint must match the region of the account and certificate profile.
2. **Identity validation.** In the account, start identity validation with the Public Trust type (individual or organization, whichever you are) and finish it. Microsoft must approve it before a certificate profile can be created; this can take days.
3. **Certificate profile.** Create a Public Trust certificate profile from the validated identity and note its name.
4. **Entra app registration.** Create an app registration (Microsoft Entra ID) with a service principal. Under Certificates and secrets, Federated credentials, add a credential for GitHub Actions with organization `Awful-Waffle-Rofl`, repository `dreamweave-launcher`, entity type Environment, name `signing`; the subject must read `repo:Awful-Waffle-Rofl/dreamweave-launcher:environment:signing`. Create no client secret. Note the application (client) ID, the directory (tenant) ID and the subscription ID.
5. **Role.** On the Artifact Signing account, Access control (IAM), add the role "Artifact Signing Certificate Profile Signer" (the name the action's README gives at tag v2.0.0) and assign it to the service principal from step 4.
6. **GitHub environment.** In the repository, Settings, Environments, create `signing`. Add yourself as Required reviewer. Under Deployment branches and tags choose Selected branches and tags and add the tag rule `v*.*.*` (the real releases) and the branch rule `main`. The `main` rule exists only so a workflow_dispatch rehearsal started from `main` is allowed (a dispatch runs on the branch ref, not on a tag); the rehearsal still needs your approval. Drop the `main` rule if you do not want rehearsals to sign.
7. **Repository variables.** Settings, Secrets and variables, Actions, Variables tab: add `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` (step 4), `ARTIFACT_SIGNING_ENDPOINT` and `ARTIFACT_SIGNING_ACCOUNT` (step 1) and `ARTIFACT_SIGNING_PROFILE` (step 3). Setting `ARTIFACT_SIGNING_ACCOUNT` is what switches signing on; deleting it switches it off again.
8. **Rehearse.** Actions, release, Run workflow from `main` with an existing tag name such as `v1.0.0`. The `package-signed` job appears and waits; approve it. When it finishes, open the "Assert signatures" step: it must list all four files as Valid and timestamped. Download the `release-assets` artifact and check the setup's Properties, Digital Signatures tab.

What this does not cover: the uninstaller that Inno Setup embeds in the setup stays unsigned. It is written on the player's PC without the downloaded-from-the-internet mark, so SmartScreen does not check it, and signing it would need Inno's SignTool hook plus the Artifact Signing client library. See `Docs/code-signing-policy.md`.

If signing fails in a real release run, no draft release is created (the failure blocks it). Fix the cause and re-run the failed jobs. Do not delete `ARTIFACT_SIGNING_ACCOUNT` just to get a release out unless you intend to ship unsigned.

## Never

- Never commit a private key (`dw-manifest-key.json` or any `keygen` output) anywhere.
- Never sign in CI, and never store the private key or its contents as a CI secret.
- Never lower `serial`. Launchers reject any manifest with a serial below the highest they have seen, and below `MinimumSerial`.
- Never edit `manifest.json` or `manifest.json.sig` in GitHub's web editor, or edit `manifest.json` without re-signing.
- Never push a manifest whose launcher url is not yet downloadable.
