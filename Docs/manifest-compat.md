# Manifest compatibility

The signed manifest is the launcher's trust anchor and its self-update pointer, so
changing its shape can strand players. These rules keep every released launcher working.

## Rules

1. **`manifest.json` stays schema 1 for the life of every v1 launcher.** A v1 launcher
   that sees any other `schema` value refuses to download (it still launches from its
   cached manifest). It also cannot read the `launcher` block any more, so it cannot
   self-update. A schema bump in `manifest.json` therefore strands every v1 install.
2. **Changes to schema 1 are additive only.** Unknown fields are ignored, and components
   whose `kind` is unknown are skipped with a warning. When a skipped component is
   marked `required`, the launcher reports it in `LauncherManifest.SkippedRequired` so it
   can ask the player to update. Never repurpose, retype or remove an existing field.
3. **A breaking schema ships as a new file.** Publish `manifest.v2.json` (and
   `manifest.v2.json.sig`) next to `manifest.json`. Launchers that understand v2 try it
   first and fall back to `manifest.json`. The schema-1 file keeps working for old
   launchers, and its `launcher` block points them at a release that understands v2.
4. **Serials are monotonic across all files.** Every published manifest gets a serial at
   least as high as any manifest published before it. The launcher refuses a serial
   below the highest it has accepted, below the serial of its cached copy, or below the
   compiled-in floor `TrustedKeys.MinimumSerial`. Raise that floor in a launcher release
   to retire old manifests that are still validly signed.
5. **Bytes are signed exactly.** Sign the file as it will be served. The repo marks
   `manifest/*.json` and `*.json.sig` as `-text` so git never rewrites line endings. The
   `manifest` branch needs the same `.gitattributes` entries.

## Publishing checklist

- Bump `serial`, keep `schema` at 1, and update `issued`.
- `ManifestTool sign --key <offline key> --manifest manifest.json`. Signing runs the
  launcher's own verifier and validation rules, so a manifest the launcher would reject
  is never signed.
- `ManifestTool verify --pub <keyId:X:Y> --manifest manifest.json`.
- Commit `manifest.json` and `manifest.json.sig` together to the `manifest` branch.
