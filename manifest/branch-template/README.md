# manifest branch

This orphan branch holds exactly two files:

- `manifest.json`
- `manifest.json.sig`

They are served to the launcher from `https://raw.githubusercontent.com/Awful-Waffle-Rofl/dreamweave-launcher/manifest/manifest.json` and `.../manifest.json.sig`.

Both are SIGNED BYTES. The signature covers the exact bytes of `manifest.json`, so changing even one character or one line ending invalidates it and every launcher then rejects the manifest.

- Never edit these files in GitHub's web editor (it can rewrite line endings or add a trailing newline).
- Never edit `manifest.json` without re-signing it, offline, and bumping `serial`.
- `.gitattributes` here sets `* -text` so git never normalizes the bytes.

The procedure is in `Docs/release-runbook.md` on the `main` branch.
