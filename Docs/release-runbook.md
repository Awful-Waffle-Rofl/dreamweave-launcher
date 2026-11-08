# Release runbook (maintainer)

The launcher is built by CI and shipped to players as a per-user Inno Setup 6 installer (`DreamWeaveLauncher-Setup-<version>.exe`, built from `installer/DreamWeaveLauncher.iss`). The download manifest is signed by hand, offline. The two are published separately: the setup as a GitHub Release, the manifest as two files on the `manifest` branch.

To check the installer without a release: every PR and push to `main` runs `build.yml`, which uploads the `DreamWeaveLauncher-Setup` artifact. To build it locally, install Inno Setup 6 and run `./tools/release/build-setup.ps1 -ExePath <DreamWeaveLauncher.exe> -OutDir <dir>` after a Release build; the script fails with a clear message if Inno Setup 6 is missing.

Commands below use `dotnet run --project tools/ManifestTool -c Release --` as `ManifestTool`. Run them from a checkout of `main` at the release tag.

## Normal release

1. **Bump the version.** Edit `<Version>` in `Directory.Build.props` (for example `1.0.1`), open a PR, merge it.
2. **Tag and push.** On the merge commit: `git tag v1.0.1` then `git push origin v1.0.1`. The tag must be exactly `v` + `<Version>`; the workflow `.github/workflows/release.yml` fails otherwise. It builds, runs the tests, checks the size and layout of the exe, builds the setup, asserts the tag equals the exe's assembly version and the setup's product version, and creates a DRAFT release with `DreamWeaveLauncher-Setup-<version>.exe` and `SHA256SUMS`. The bare exe is not a release asset. To rehearse without a release, dispatch the workflow by hand with a tag input.
3. **Download and verify.** From the DRAFT release download both files. In PowerShell: `Get-FileHash .\DreamWeaveLauncher-Setup-1.0.1.exe -Algorithm SHA256` must equal the (case-insensitive) hash in `SHA256SUMS`.
4. **Publish the release.** In GitHub, open the draft and publish it. A draft's asset URLs return 404 to everyone, so the release must be public before any manifest points at it. Confirm the download URL `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/download/v1.0.1/DreamWeaveLauncher-Setup-1.0.1.exe` now downloads. Players install by running that setup.
5. **Print the launcher block.** `./tools/release/print-manifest-launcher-block.ps1 -ExePath .\DreamWeaveLauncher.exe -Version 1.0.1`. It prints `version`, `url` (the published release's download URL for the tag), `sha256` (uppercase) and `size`.
6. **Edit `manifest.json`.** In a worktree of the `manifest` branch (see "Creating the manifest branch"), replace only the `version` line of the `launcher` object with the printed one; the printed `url`, `sha256` and `size` are obsolete (the launcher no longer downloads itself), so do not copy them. Keep `minVersion` and `downloadPage` (the page players open to get the new setup, normally `https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest`), and delete any leftover `url`, `sha256`, `size` or `authenticodeSubject` lines. Increment `serial` by at least 1 and update `issued`. Edit with an editor that keeps the file's bytes sane (no BOM, no reformat on save).
7. **Sign, offline.** On the maintainer's PC only: `ManifestTool sign --key <path to private key> --manifest <path to manifest.json>`. This writes `manifest.json.sig` next to it.
8. **Verify.** `ManifestTool verify --pub <keyId:X:Y or public key file> --manifest <path to manifest.json>` must print `OK` with the serial you expect.
9. **Push the manifest.** Commit `manifest.json` and `manifest.json.sig` together to the `manifest` branch and push it. Confirm both raw URLs serve the new bytes (CDN caching can lag a few minutes; the launcher retries once with a cache-bust).

v1 ships unsigned (no Authenticode); see `Docs/code-signing-policy.md`.

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

## Never

- Never commit a private key (`dw-manifest-key.json` or any `keygen` output) anywhere.
- Never sign in CI, and never store the private key or its contents as a CI secret.
- Never lower `serial`. Launchers reject any manifest with a serial below the highest they have seen, and below `MinimumSerial`.
- Never edit `manifest.json` or `manifest.json.sig` in GitHub's web editor, or edit `manifest.json` without re-signing.
- Never push a manifest whose launcher url is not yet downloadable.
