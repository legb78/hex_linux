# Security Policy

## Supported versions

Only the [latest release](https://github.com/legb78/hex_linux/releases/latest)
is supported. Fixes go out as a new version; older ones are not patched.

## Reporting a vulnerability

**Do not open a public issue.** Use
[Report a vulnerability](https://github.com/legb78/hex_linux/security/advisories/new)
in the Security tab. The report stays private between you and the maintainer
until a fix ships.

Expect a first reply within a week. This is a one-person project run on spare
time, so there is no bounty and no guaranteed turnaround — but a real report
will be taken seriously, and you will be credited in the advisory unless you
ask otherwise.

Useful in a report: the version, the distribution, desktop and session type,
what an attacker gains, and the shortest path you know to reproduce it.

## What HexLinux can reach, and why

A dictation tool on Linux has to be granted more than most applications, and
the permissions involved are broader than HexLinux itself. They are stated
here plainly, because they are the price of the feature, not a detail.

### The keyboards: `/dev/input`

Holding a key to dictate means seeing that key whichever window has the focus,
and on Linux that means reading the keyboard devices under `/dev/input`, below
the desktop. HexLinux reads them passively: it never grabs a device, so every
key still reaches the desktop, and it looks at the keys of the shortcut only.
It never stores or logs a keystroke.

The permission to read those devices is another matter, and it is not
HexLinux's alone:

- **The udev rule** installed by `install-udev-rules.sh` tags the keyboards
  `uaccess`, so that systemd-logind grants the user of the active local session
  access to them. That access is **read and write**, and it is granted to
  **every process of that user**, not to HexLinux: any program you run can then
  read everything typed on those keyboards — passwords and `sudo` prompts
  included, which a malicious program can turn into root access — and inject
  keystrokes by writing to the same devices. On Wayland, it undoes precisely the
  isolation between applications that the compositor provides. Security keys
  and barcode readers present themselves as keyboards, so what they send
  becomes readable too.
- **The `input` group**, the other usual way, is broader still: permanent read
  and write access to **every** input device, in every session, including
  while another user is at the machine. HexLinux never adds anyone to it.
- **No permission at all** is an option: bind `hexlinux --toggle` to a shortcut
  of your desktop, press it to start and again to stop. The desktop handles the
  key; HexLinux never reads a keyboard.

### The virtual keyboard: `/dev/uinput`

Under GNOME and KDE on Wayland, no ordinary program may send keys to another
window, so the paste shortcut can only be sent through a virtual keyboard
created with `/dev/uinput`. The rule that allows it is **optional** and
installed separately. Whoever can write to `/dev/uinput` can create keyboards
and mice and type anywhere — the lock screen, a root terminal, a `sudo` prompt
— and, again, that is every process of the user, not only HexLinux. HexLinux's
own virtual keyboard sends nothing but the paste shortcut.

### The clipboard

In `Paste` mode, the dictation goes through the clipboard: HexLinux saves what
was there, puts the text in, sends the paste shortcut, waits about 400 ms for
the application to read it, then puts the previous content back — or clears
the clipboard when that content could not be saved. It never touches the
X11 primary selection.

During those 400 ms, the dictation is exposed like anything else you copy:

- **Clipboard history managers** — Klipper (on by default in KDE Plasma), GNOME
  Shell extensions, cliphist, CopyQ — record it, some of them on disk. On
  Windows, HexWin marks its dictations to keep them out of the clipboard
  history. **HexLinux cannot**: neither `wl-copy` (wl-clipboard 2.2.1, the
  version of Ubuntu 24.04) nor `xclip` can offer the password-manager hint that
  some managers honour alongside the text, so the dictation is recorded like
  any other copy. Klipper's defaults go further: it keeps its history from one
  session to the next, and refuses an empty clipboard — which undoes the
  clearing described above.
- Under X11, any client can read the clipboard while it holds the dictation.

`Type` mode avoids the clipboard entirely, where the desktop allows it.

`clipboardFallback`, off by default, **deliberately leaves the dictation in the
clipboard** when no way to send keys is available, with a notification asking
you to paste it yourself. A history manager then keeps it. That is the reason
it is opt-in.

With `pasteShortcut` set to `ShiftInsert`, xterm and terminals of its family
paste the **primary selection** — the last text you selected — rather than the
clipboard: not the dictation, and possibly something you did not mean to paste.

### Audio and transcripts

Both stay on the machine: HexLinux makes no network connection at all, and the
recording lives in memory. The log records durations, the audio level and the
number of characters, never the text. The dictated text is never passed on a
command line — visible to every local user in `/proc/<pid>/cmdline` — but
through standard input, to the tool that types or copies it.

### The control socket and the session bus

`hexlinux --toggle` and its siblings talk to the running daemon through a Unix
socket in `$XDG_RUNTIME_DIR/hexlinux/`, a directory only you can open. The
daemon checks that the other end runs as the same user, and understands five
words — `toggle`, `start`, `stop`, `cancel`, `status` — none of which carries
text. The tray menu, on the session bus, offers the same actions plus the two
switches and Quit. Any process of yours can therefore start a dictation; it
could already record the microphone directly, so nothing is gained. Nothing a
caller sends is used as a path or as text.

### The lock screen and other sessions

The kernel delivers keys from `/dev/input` whatever the desktop is doing: on
the lock screen, and while another user's session is in front. Before starting
a dictation and again before inserting it, HexLinux asks systemd-logind whether
its session is active and unlocked, and refuses otherwise. That relies on the
screen locker telling logind: GNOME and KDE do; swaylock and hyprlock, as far as
their source shows, do not, and on those the guard cannot see the lock. Where
logind is absent altogether (a container), insertion is allowed; where it is
present but cannot be read, insertion is refused.

### Scripts that run with more rights, or fetch code

- **`get-model.sh`** downloads the recognition model over HTTPS only, redirects
  included, to a temporary file. It checks the size and the SHA-256 pinned in
  the script **before** extracting anything, then extracts only the four
  expected files. If the upstream archive is ever republished, the check fails
  and nothing is installed: the pinned values have to be updated in the script,
  in a reviewed change. A mismatch is a stop, never a warning.
- **`install-udev-rules.sh`** runs as root. It writes fixed content to
  `/etc/udev/rules.d` — shown before it is installed — and removes it again with
  `--uninstall`.
- **The release** is built by CI from `main`, after the unit tests pass again.
  The job that builds has a read-only token; the job that publishes holds the
  only write token, runs no code from the repository, and uses no third-party
  action. Release tags cannot be moved or deleted. Check what you download:

  ```sh
  sha256sum -c SHA256SUMS
  gh release view vX.Y.Z --repo legb78/hex_linux --json assets --jq '.assets[] | .name + " " + .digest'
  ```

  The second command shows the digest GitHub computed when the file was
  uploaded, which does not depend on the files of the release page.

The branch rules allow no bypass: nobody, the owner included, can push to
`main` or `develop` directly or merge without green checks. That protects
against mistakes and against other collaborators — not against the owner's own
account or token being stolen, since an administrator can change the rules.

## What is worth reporting

- Anything that makes HexLinux read, keep, log or forward keystrokes beyond the
  keys of its shortcut, or send any key other than the paste shortcut.
- A way to get dictated text inserted into a locked screen or another user's
  session, or written anywhere but the focused application — a log, a file, a
  command line, a notification.
- Audio or transcripts leaving the machine.
- A way to make the control socket or the tray act on data sent by the caller,
  or to reach them from another user.
- A way for `get-model.sh` to install anything other than the pinned archive,
  or for `install-udev-rules.sh` to write anything other than its fixed rules.
- Files or folders HexLinux creates readable by other users, or an autostart
  entry pointing at something other users can replace.
- A weakness in the release chain that could put another file in a release.

## What is not a vulnerability

- **The scope of the permissions above**, once granted as documented: that any
  process of the user can read or inject keystrokes through the udev rule, or
  through the `input` group, is the documented trade-off. A way for HexLinux to
  widen it is a vulnerability.
- **The keys of the shortcut reaching the desktop.** They are observed, not
  intercepted, by design.
- **Insertion refused by a Wayland compositor** without the virtual keyboard.
  That is Wayland doing its job.
- **Clipboard history managers recording a dictation in Paste mode**, as stated
  above.
- **Actions a process of the same user can trigger** through the control socket
  or the tray: start or stop a dictation, switch the tones or the autostart,
  quit.
- **The binary not being signed.** Its integrity rests on GitHub, HTTPS and the
  checksums above.
