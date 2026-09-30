---
name: Bug report
about: Report something not working
labels: bug
---

## What happens

<!-- What you observe, and what you expected instead. -->

## Diagnostic output

HexLinux ships one diagnostic mode per layer. Run them in this order and paste
what they print: that is what tells us which layer to look at, instead of
guessing. Run them from the folder holding `hexlinux`, in the same desktop
session as the problem — not over SSH, which has no display to insert into.

```sh
# 0. What does HexLinux see? Session type, keyboards and their permissions,
#    uinput, tools, microphone, model, insertion plans, screen lock state.
./hexlinux --doctor
```

```
<!-- paste the output -->
```

```sh
# 1. Is the microphone picking anything up?
./hexlinux --record test.wav
```

```
<!-- paste the output -->
```

```sh
# 2. Does the engine transcribe that file?
./hexlinux --transcribe test.wav
```

```
<!-- paste the output -->
```

```sh
# 3. Does the hotkey fire? (skip it if you dictate through hexlinux --toggle)
./hexlinux --watch-hotkey
```

```
<!-- paste the output -->
```

```sh
# 4. Does insertion reach the window? Click into a text field during the delay.
./hexlinux --inject "some text"
./hexlinux --inject "some text" --mode Type
```

```
<!-- paste the output -->
```

## Log

Contents of `~/.local/state/hexlinux/hexlinux.log` (or
`$XDG_STATE_HOME/hexlinux/hexlinux.log` if you set that variable), and
`crash.log` next to it if it exists.

Each dictation records its duration, the **captured audio level** and the number
of characters produced — never the text. Those three numbers together usually
locate the fault.

```
<!-- paste the last lines -->
```

## Environment

- Distribution and version (`grep PRETTY_NAME /etc/os-release`):
- Desktop (`echo $XDG_CURRENT_DESKTOP`):
- Session type (`echo $XDG_SESSION_TYPE`):
- Kernel (`uname -r`):
- Sound server (`pactl info | grep 'Server Name'`):
- Keyboard layout (e.g. AZERTY, QWERTZ, Dvorak):
- HexLinux version (the release you downloaded, or the commit you built):
- How HexLinux is started (terminal, `--autostart on`, systemd unit):
- Permission for the hotkey (udev rule, `input` group, none):
- `~/.config/hexlinux/settings.json` (with anything personal removed):
