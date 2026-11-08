# Launching the game client

The launcher starts `acclient.exe` itself (`GameLauncher`) with an argument string built from the
signed manifest's `launch.argTemplate` (`LaunchArgsBuilder`).

## What the launcher does

- Arguments come from the template, default `-a "{user}" -v "{pass}" -h {host}:{port} -rodat off`.
  `-rodat off` is what lets several clients run against the same game folder at once, and it is what
  multi-boxing players already use.
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

- It never opens a process by id. It uses the process handle CreateProcess returned for the client
  it just started (`StartedClient`, kept open until the release is done), so a reused process id
  can never be touched. That handle carries the duplicate-handle and full query rights the handle
  table query needs (the limited query right is refused with `STATUS_ACCESS_DENIED`). It reads that
  one process's handle table (`NtQueryInformationProcess`, `ProcessHandleInformation`), never the
  system-wide table, and never queries object names.
- It opens its own handle to `Empyrean Client`, duplicates only the client's handles of the same object
  type, and matches by identity (`CompareObjectHandles`). It closes its own handles BEFORE closing the
  client's (`DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE`); otherwise its own handle would keep the
  object alive and the next client would still be refused.
- After the close it opens the lock once more: if it still exists, another process holds it and the
  next client would be refused anyway (reported as held elsewhere).
- It polls for up to 20 s for the client to create the lock; the status line reads "Starting
  <row>..." meanwhile. Play and Play all wait for the release to finish before the next start.
  Closing the window stops the wait between looks; a close of a lock already found always completes.
  Every outcome is one `launch client-lock pid=...` line in launcher.log.
- If the release does not succeed (not found in time, client exited, held elsewhere, Windows older
  than 10, a Windows call failed, or the window closed during the wait), the client keeps running
  and its row says: "Started, but a second client may be refused. Close the other client to play
  this one."
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

## Launch with Decal (optional)

Players who already have [Decal](https://www.decaldev.com/) installed can tick "Launch with Decal" to
start the client with Decal and its plugins loaded, the same way ThwargLauncher, Mag-ACClientLauncher
and RynthCore do. The option appears only when Decal is detected on the PC, and is off by default. When Decal is not
detected, a "Get Decal" link to its download page is shown in its place.
Decal is the player's own software: the launcher never bundles, downloads, installs, modifies or
configures it. This is a narrow owner exemption to the project's no-injection rule, for this one
purpose.

- **Detection (`DecalLocator`).** Read-only. It reads the Decal agent folder from the registry value
  `AgentPath` under `HKLM\SOFTWARE\Decal\Agent` (the 32-bit view, so `WOW6432Node` on 64-bit Windows),
  appends `Inject.dll`, and shows the option only when that file exists. Nothing is written.
- **The helper (`DreamWeaveDecalLaunch.exe`).** An isolated x86 executable installed beside the
  launcher. The main launcher imports no injection API (`VirtualAllocEx`, `WriteProcessMemory`,
  `CreateRemoteThread`, and so on); those live only in the helper, and a test asserts the main
  assemblies declare none of them. The helper takes the game folder and the `Inject.dll` path as its
  two command-line arguments, and reads the acclient argument string (which contains the password)
  **on stdin, never on its command line**. It prints the started process id on stdout. It validates
  its inputs before starting anything: the game folder must contain `acclient.exe`, the `Inject.dll`
  argument must be an existing absolute path named `Inject.dll`. Links are checked like this: the game
  folder itself may be a link (a moved install), but `acclient.exe` may not be one, and the `Inject.dll`
  path and every folder above it, below the drive root, may not be a link. The helper starts nothing but
  `<gameFolder>\acclient.exe`.
- **Sequence.** The helper creates acclient suspended, loads the player's `Inject.dll` into it and
  calls that library's documented `DecalStartup` entry point, then resumes the client. If any
  injection step fails it still resumes the client (fail-open), so the player gets a plain client
  rather than a hung one, and the launcher shows a notice that the game started without Decal.
- **Decal owns the single-instance check.** A Decal launch does **not** go through
  `ClientLockReleaser`: once `DecalStartup` has run, `Inject.dll` itself handles the single-instance
  check. The process-by-id lookup used to hand the started client back to the launcher lives only in
  the Decal start seam and is never reachable from `ClientLockReleaser` or the client-handle API.
- **The password** reaches the client exactly as on the normal path (the same `LaunchArgsBuilder`
  output), but is passed to the helper on stdin so it never appears on the helper's command line.

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