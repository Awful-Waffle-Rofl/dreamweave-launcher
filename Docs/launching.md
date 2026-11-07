# Launching the game client

The launcher starts `acclient.exe` itself (`GameLauncher`) with an argument string built from the
signed manifest's `launch.argTemplate` (`LaunchArgsBuilder`).

## What the launcher does

- Arguments come from the template, default `-a "{user}" -v "{pass}" -h {host}:{port} -rodat on`.
  `-rodat on` lets a new client share dat files with clients that are already running.
- The client is started with `UseShellExecute = false` and its working directory set to the game
  folder. The launcher never requests elevation and never touches other `acclient.exe` processes.
- User and password are rejected (with a plain-English message) rather than escaped when they could
  break out of their quotes: a double quote, any control character, a trailing backslash, or (user
  only) whitespace. Limits: user 1-50 characters (the server closes logins over 50 and the account
  column is 50 wide), password 1-64 (the server has no explicit limit; 64 is the launcher's cap).
  Host must be a host name, IPv4 address or bracketed IPv6 address; port 1-65535.
- `BuildRedacted` returns the same string with the password masked; use it for any log line. The
  launcher never logs the real command line and never puts the password in an error message.
- The password is non-ASCII safe in the credential store, but the AC client is an ANSI program, so
  passwords with characters outside the system code page may not reach the server intact.

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