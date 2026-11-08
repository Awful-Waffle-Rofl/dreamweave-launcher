# Launching the game client

The launcher starts `acclient.exe` itself (`GameLauncher`) with an argument string built from the
signed manifest's `launch.argTemplate` (`LaunchArgsBuilder`).

## What the launcher does

- Arguments come from the template, default `-a "{user}" -v "{pass}" -h {host}:{port} -rodat on`.
  `-rodat on` lets a new client share dat files with clients that are already running.
- The client is started with `UseShellExecute = false` and its working directory set to the game
  folder. The launcher never requests elevation and never touches `acclient.exe` processes it did
  not start.

## Releasing the single-instance lock (multi-client)

acclient.exe refuses to run ("There is a client already running on this machine") when the named
semaphore `Empyrean Client` already exists: it calls `CreateSemaphoreA(NULL, 0, 1, "Empyrean Client")`
and checks for `ERROR_ALREADY_EXISTS`. So after each client it starts, `ClientLockReleaser` closes that
one handle inside that one process, so the next row can start. Owner ruling, a narrow exemption: no
client file is modified, no code is injected, no Decal is used, and only a process the launcher itself
just started is ever opened.

- It opens the started pid with `PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION` only (the handle table
  query is refused with the limited query right) and reads that one process's handle table
  (`NtQueryInformationProcess`, `ProcessHandleInformation`). It never reads the system-wide table and
  never queries object names.
- It opens its own handle to `Empyrean Client`, duplicates only the client's handles of the same object
  type, and matches by identity (`CompareObjectHandles`). It closes its own handles BEFORE closing the
  client's (`DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE`); otherwise its own handle would keep the
  object alive and the next client would still be refused.
- It polls for up to 20 s for the client to create the lock. Play and Play all wait for the release to
  finish before the next start. Every outcome is one `launch client-lock pid=...` line in launcher.log.
- If the release does not succeed (not found in time, client exited, Windows older than 10, a Windows
  call failed), the client keeps running and its row says: "Started, but a second client may be
  refused. Close the other client to play this one."
- User and password are rejected (with a plain-English message) rather than escaped when they could
  break out of their quotes: a double quote, any control character, a trailing backslash, or (user
  only) whitespace. A password containing a space is also rejected when the template does not wrap
  `{pass}` in double quotes. Limits: user 1-50 characters (the server closes logins over 50 and the
  account column is 50 wide), password 1-64 (the server has no explicit limit; 64 is the launcher's
  cap). Host must be a host name or IPv4 address (no bracketed IPv6: client support is untested);
  port 1-65535. Problems with the host, port or template come from the signed manifest and are
  reported as `ManifestInvalid`; only user and password problems are `InvalidCredentials`.
- `BuildRedacted` returns the same string with the password masked; use it for any log line. The
  launcher never logs the real command line and never puts the password in an error message.

## Printable ASCII only (account name and password)

The account name may use characters 0x21-0x7E and the password 0x20-0x7E (letters, digits, standard
symbols; the password may also contain spaces). Anything else, including accented letters, CJK text
and emoji, is refused before launch.

Why: the server creates an account automatically on first login from whatever bytes arrive
(`Source/ACE.Server/Network/Handlers/AuthenticationHandler.cs:90-110` in the server repo, which
lowercases the name and hashes the password as received), and the client-to-server string path is
not Unicode-safe. A non-ASCII password could therefore create an account that the player can never
log into again, with no error shown. Refusing it up front is the only safe behavior.
## Saved passwords

"Remember me" uses the Windows Credential Manager (generic credential, target `DreamWeave/<account>`,
persistence `LOCAL_MACHINE`: kept across log-off and reboot, never roamed to other computers). The
launcher writes no password to any file of its own.

## Privacy statement requirement

**The password is visible on the client's command line to other processes running as the same
Windows user** (for example Task Manager's command-line column, WMI, or any program that reads
process command lines). This is inherent to the Asheron's Call client, which accepts the password
only as the `-v` argument; other launchers for the game have the same exposure. The launcher cannot
remove it. The future privacy statement must say so in plain words.