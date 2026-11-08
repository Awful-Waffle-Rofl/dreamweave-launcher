# Manifest compatibility

The signed manifest is the launcher's trust anchor and its launcher-update pointer, so
changing its shape can strand players. These rules keep every released launcher working.

## Rules

1. **`manifest.json` stays schema 1 for the life of every v1 launcher.** A v1 launcher
   that sees any other `schema` value refuses to download (it still launches from its
   cached manifest). It also cannot read the `launcher` block any more, so it cannot
   offer the player a launcher update. A schema bump in `manifest.json` therefore strands every v1 install.
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

## Optional fields added within schema 1

These were added under rule 2 and are safe to publish: a launcher that predates one ignores it.

- **`mirrors`** (on a component): fallback download sources, tried in order after the primary
  `transport`/`url`/`sourceLabel`. Each entry is `{ "transport": "mega" | "http", "url": "...",
  "sourceLabel": "..." }`. `http` needs an absolute `https` URL; `mega` needs a public file link
  (`https://mega.nz/file/<id>#<key>`). Every source must serve the bytes described by the
  component's single `size` and `sha256`: a mirror never carries its own hash, and a download whose
  hash differs is deleted and the next source is tried. An older launcher ignores `mirrors` and
  uses the primary only, so keep the primary the source most players can reach.
  A mirror whose transport this launcher does not know is skipped with a warning, so a newer
  manifest can add transports; a known transport with a bad URL, or a mirror that repeats the
  primary or an earlier mirror (same transport and URL), makes the manifest invalid.
  Each source keeps its own in-progress file, and a corrupt mega download falls back to the next
  source at once instead of being retried.
- **`launcher.downloadPage`**: the web page where players get the latest setup, for example
  `"https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest"`. Optional, but an update is
  offered (Optional or Mandatory) only when `launcher.version` is newer than the running launcher AND this
  is an absolute `https` URL; a non-empty value that is not an absolute `https` URL makes the manifest
  invalid. The launcher only opens the page in the default browser: it never downloads, replaces or
  restarts itself. There is no host allowlist, because the manifest is signature-verified. The older
  `launcher.url`, `sha256`, `size` and `authenticodeSubject` members are obsolete: they still parse (so older
  and newer manifests both load) but are no longer validated or read, and new manifests should omit them.
  A launcher that predates `downloadPage` ignores it.
- **`defaultSelected`** (already present) now also sets the starting checkbox of an optional
  component: `true` starts checked, `false` or absent unchecked. Required components are unchanged.

## Reaching launchers built before downloadPage

No public install predates `downloadPage` (v1.0.0 was never released), so this only matters for internal builds.

- A launcher built before this change ignores `downloadPage`. The only way to reach it is to raise `launcher.minVersion`, which shows it the "download again from the website" banner and blocks Play.
- Never publish a build of the current code as a bare exe at `launcher.url`. An old launcher would download it, swap it in and start it with `--updated`, which the current launcher rejects and exits silently, leaving the player with a launcher that does not start.

## Publishing checklist

- Bump `serial`, keep `schema` at 1, and update `issued`.
- `ManifestTool sign --key <offline key> --manifest manifest.json`. Signing runs the
  launcher's own verifier and validation rules, so a manifest the launcher would reject
  is never signed.
- `ManifestTool verify --pub <keyId:X:Y> --manifest manifest.json`.
- Commit `manifest.json` and `manifest.json.sig` together to the `manifest` branch.
