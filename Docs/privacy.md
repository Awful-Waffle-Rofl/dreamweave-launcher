# DreamWeave Launcher privacy statement

DreamWeave Launcher can send anonymous setup diagnostics to help us find where installs fail. This is OFF unless you tick the box on the Welcome screen or in Settings.

When it is on, each event contains only:
- a random install ID, created on your PC and not linked to your account (it is replaced if you turn diagnostics off and on again)
- a random ID for each time the launcher starts, and the event's position in that run, so one session's events can be grouped
- the launcher version and your Windows build number
- which setup step ran (for example "launch" or installing one game component), whether it started, worked, failed or was cancelled, an error code from a fixed list, and how long it took

It never contains your account name, password, file paths, PC name, Windows user name, or free-form error text.

Events are sent over HTTPS to the DreamWeave website and kept for 90 days, then deleted. Like any website, the server that receives them may see your IP address while the connection is open.

Your account name and saved passwords stay on your PC, in Windows Credential Manager. The launcher sends them only to the DreamWeave game server when you click Play, the same way the game client always has.

Turning diagnostics off deletes any events that have not been sent yet; events already received are kept for up to 90 days, then deleted. You can change this at any time in Settings.

Questions: ask in the DreamWeave Discord.
