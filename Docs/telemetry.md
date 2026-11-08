# Launcher telemetry: what is sent, what is kept, how to turn it off

Factual inputs for the player-facing privacy text. Server side contract: `Docs/Market/launcher-telemetry-v1.md`
in the dreamweave-market repository. Code: `src/Launcher.Core/Telemetry/`.

## It is off unless the player turns it on

- Telemetry is **opt-in**. A fresh install has it off (`telemetryOptIn: false` in `state.json`).
- Even when the player opted in, nothing is recorded, queued or sent unless the signed manifest also names a
  telemetry endpoint (`telemetry.endpoint`). A manifest with no endpoint switches telemetry off for everyone.
- The endpoint must be `https` on the default port, with no query string, no fragment and no credentials, and its
  host must be in the same registrable domain as the game server host in the manifest (the last two DNS labels, or
  three for suffixes such as `co.uk`), or be on an explicit allow list compiled into the launcher. Anything else is
  ignored and telemetry stays off.

## What is sent

One HTTPS `POST` of a small JSON body per batch (at most 50 events and 16 KiB) to the endpoint:

| Field | Meaning |
|---|---|
| `schema` | Always `1`. |
| `installId` | A random GUID created on first run. Not derived from the machine, the player or the account. |
| `runId` | A new random GUID for each launcher start, so a run's events can be grouped. |
| `launcherVersion` | The launcher's own version, for example `1.4.0`. |
| `osBuild` | The Windows build number only, for example `26200`. No edition, language or hardware. |
| `events[].seq` | Position of the event within the run (0, 1, 2, ...). |
| `events[].step` | Which install step: a short fixed id such as `manifest`, or a manifest component id. |
| `events[].outcome` | `started`, `succeeded`, `failed` or `cancelled`. |
| `events[].error` | A number for the kind of failure (the launcher's error code), `0` for none. |
| `events[].durationMs` | How long the step took in milliseconds, or `null` if not measured. |

**Never sent:** account name, password, character names, the game folder or any file path, file names, IP
address (the server does not store it), computer name, user name, hardware details, or anything typed by the player.
The request carries no cookies and no credentials, uses the generic user agent `DreamWeaveLauncher`, and puts nothing
identifying in the URL. Redirects are not followed.

## What the server keeps

Per the contract: one line per event with the hour of receipt (not the exact time), the install id, run id, launcher
version, OS build, sequence number, step, outcome, error and duration. The server stores no address, no user agent and
no headers. Daily files are deleted after 90 days.

## What the launcher keeps on the player's computer

- `%LOCALAPPDATA%\DreamWeave\state.json` holds the opt-in choice and the install id.
- `%LOCALAPPDATA%\DreamWeave\telemetry-queue.json` holds events that have not been delivered yet, so a failed send
  or a closed launcher does not lose them. It holds the same fields as above plus the time each event happened. It
  never holds the install id (that is read from `state.json` when a batch is sent).
- `%LOCALAPPDATA%\DreamWeave\launcher.log` (plus at most one older `launcher.log.1`, each capped at about 1 MB) records
  each download attempt: component id, source type, host name, outcome, bytes, duration and the error type and
  message. It is local only and is never sent. The Windows profile folder is written as `%USERPROFILE%`, and it never
  holds an account name or password. Uninstall deletes it with the rest of the folder.
- The queue keeps at most 200 events; the oldest are dropped first. An event older than 7 days is dropped without
  being sent.

## How sending behaves

- Recording an event never slows the launcher: it is added to a queue and a background task sends it a few seconds
  later, in batches, with at least a few seconds between requests.
- If the server answers 429 (too many requests) or 503 (busy) the launcher waits for the server's `Retry-After` time
  and tries that batch **once more**; if it is still refused the events stay queued for a later start.
- A 503 whose body says disabled (the server has no storage configured) is not retried at all, and the launcher stays quiet for the rest of that run (the events stay queued).
- Any other answer (for example 400, 413, 415) means the batch is dropped and not retried.
- If the network is unreachable the events stay queued and sending pauses for a minute.
- The launcher never retries in a loop, and a telemetry failure never delays or fails a launch.

## How to turn it off

Turning telemetry off (the settings toggle calls `TelemetryClient.SetOptIn(false)`):

1. stops recording immediately;
2. deletes the queued events in memory and the `telemetry-queue.json` file;
3. saves `telemetryOptIn: false`;
4. replaces the install id with a new random one, so nothing the server already holds can be tied to anything sent
   later (if the player turns telemetry back on, it starts as a new anonymous install).

The opt-out is also recorded in a small marker file, %LOCALAPPDATA%\DreamWeave\telemetry-optout, written before the settings file is touched. If the settings file is locked or unwritable, telemetry still stays off in this run and after a restart; turning telemetry on again removes the marker.

A request already on its way when the player turns telemetry off cannot be recalled. Data the server has already
received is anonymous (it is keyed only by the old install id) and cannot be looked up by the player; it expires
after 90 days.
