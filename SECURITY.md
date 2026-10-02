# Security policy

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's
**Security → Report a vulnerability** on this repository, not in a public
issue. Include what you found, how to reproduce it, and what an attacker could
do with it. You should hear back within a week.

Only the latest release is supported; there are no maintained release
branches.

## What is in scope

A local app with no network service and no elevated part, so the interesting
boundaries are these:

- **The speed test**, the only traffic the app sends: HTTPS to
  speed.cloudflare.com and ICMP echoes to it, only when a test is started.
  Anything that makes the app send traffic without one, or anywhere else, is
  in scope.
- **The link between the app's two processes**: a named pipe the meter reads
  commands from (reload settings, show a notification, exit) and a shared
  memory block it publishes its live reading in, both per user session.
  Anything that lets another account drive the meter, or that turns a command
  into more than those three, is in scope.
- **The files it writes**: `settings.ini`, the history and
  `speedtests.jsonl`, all under the user's own profile or a folder the user
  chose. Anything that makes the app write outside them is in scope.
- **The installer and uninstaller** (`tools\Install.ps1`, `Uninstall.ps1`),
  which run as the user and touch only the per-user program folder, Start
  menu, Run key and Installed apps entry.

Out of scope: anything requiring an already-elevated attacker, physical access
to an unlocked machine, or a history folder the user deliberately placed where
other accounts can write.
