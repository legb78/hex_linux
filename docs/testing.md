# Testing HexLinux

HexLinux is built and unit-tested **anywhere Linux runs a .NET 10 SDK** — WSL,
a CI runner, any distribution. That covers the pure logic, where the decisions
are. The rest — the keyboard, the virtual keyboard, the desktop's tray and
notifications, the screen lock — needs **a real Linux desktop session**: a
virtual machine, or real hardware. WSL can go further than the build, but not
that far, and this page says exactly where the line is and why.

| Layer | WSL (WSLg) | VM or real hardware |
|-------|:----------:|:-------------------:|
| Build, unit tests, coverage, formatting | yes | yes |
| Engine with the real model (`--transcribe`, integration tests) | yes | yes |
| Microphone (`--record`), tones (`--test-feedback`) | yes, through WSLg's PulseAudio server | yes |
| Insertion into X11 windows (xdotool, xclip) | yes, through XWayland | yes |
| Clipboard save and restore | yes, but it is the Windows clipboard | yes |
| Daemon and control socket (`--toggle`, `--status`) | yes | yes |
| Tray and notifications, D-Bus protocol level | only with a stand-in on a private bus | yes, for real |
| wlroots insertion (wtype, wl-clipboard's data-control) | only on a Sway without a screen | yes, on a wlroots desktop |
| Keyboard (hold-to-talk, `--watch-hotkey`) | **no** | yes |
| Virtual keyboard (uinput), udev rules | **no** | yes |
| Paste under GNOME or KDE on Wayland (needs uinput) | **no** | yes, per desktop |
| Screen lock and user switching (session guard) | **no** | yes |
| Autostart at login | **no** | yes |

## What WSL can test

### Setting up

```sh
scripts/setup-dev.sh                  # .NET 10 SDK in ~/.dotnet, runtime tools
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
scripts/get-model.sh                  # about 490 MB, once
dotnet build -c Release
dotnet test                           # unit and integration tests
```

Three things about this WSL environment are easy to trip over:

- **With systemd enabled, the Wayland socket is not where the environment says.**
  `WAYLAND_DISPLAY=wayland-0` and `XDG_RUNTIME_DIR=/run/user/0/` point at
  `/run/user/0/wayland-0`, which does not exist: with `systemd=true`, logind
  mounts its own `/run/user/0` (a `tmpfs`, mode 700), over WSLg's. The socket
  lives in `/mnt/wslg/runtime-dir/`. Every Wayland client fails until told:

  ```sh
  export WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0
  ```

  (`XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir` works as well, but also moves
  HexLinux's control socket.) X11 needs nothing: `DISPLAY=:0` reaches WSLg's
  XWayland server.
- **WSLg's clipboard is the Windows clipboard.** WSLg's own description lists
  "clipboard integration for copy/paste" between Windows and Linux
  applications. A clipboard test in WSL overwrites what you copied in Windows,
  and, with Windows' clipboard history on, presumably lands in it (Win+V): save
  what you had first, and use test text you do not mind keeping there.
- **Insertion needs a logind session.** Before inserting, HexLinux asks logind
  about the user's session. A WSL shell opened from a Windows terminal is one
  (`loginctl list-sessions` lists it: a `tty` session with no seat); a command
  run with `wsl.exe --exec`, while no WSL terminal is open, belongs to none, and
  every insertion is then refused: "logind did not answer about the user's
  display session: refused to be safe" (verified on 2026-09-29:
  `loginctl show-user 0` answers "User ID 0 is not logged in or lingering").
  Keep a WSL terminal open while testing, or open a session for the time of
  the tests with `wsl.exe -d Ubuntu -- sleep 900`.

### Checks that run in WSL

| Check | Command | Expected |
|-------|---------|----------|
| Everything builds, tests pass | the four CI commands in [CONTRIBUTING.md](../CONTRIBUTING.md#tests) | 0 warnings, format clean, coverage at least 75 % |
| Engine | `./hexlinux --transcribe tests/HexLinux.Tests/Fixtures/bonjour-fr.wav` | a French sentence starting with "Bonjour", and its duration |
| Engine, wrong file | `--transcribe` of a text file, or of a 44.1 kHz stereo WAV | "not a WAV file", or the format found and how to convert it; exit code 1, no crash |
| Microphone | `./hexlinux --record /tmp/t.wav --seconds 3` | a 16 kHz mono WAV and its level; a silent result says so (check the Windows microphone privacy settings) |
| Tones | `./hexlinux --test-feedback` | two tones through the Windows speakers; with `PULSE_SERVER=unix:/nonexistent`, "No tone could be played" and exit code 3 |
| Diagnosis | `./hexlinux --doctor` | Wayland or X11 session, no `/dev/input`, no uinput, tools found, audio reachable, the insertion plans |
| Insertion, Type, X11 | `xterm -e sh -c 'cat > /tmp/out.txt' &` then `./hexlinux --inject "hello" --mode Type --sender xdotool`, clicking into the xterm during the delay, then Enter and Ctrl+D | `/tmp/out.txt` holds `hello` |
| Clipboard restore | copy something, run `./hexlinux --inject "hello"` into a text field, then paste elsewhere | the dictation landed; the clipboard holds what you had copied |
| Daemon and socket | `./hexlinux &` then `./hexlinux --status`, `./hexlinux --toggle` twice | `idle`, then a dictation from the microphone inserted at the focus |
| Single instance | a second `./hexlinux` while the first runs | "already running"; the first one keeps going |
| Stop during a transcription | `--toggle`, speak twenty seconds, `--toggle`, then at once `kill -TERM` the daemon | exit code 0 and "stopped" in the log a few seconds later, no `crash.log`, no core dump |

Type mode under WSLg's XWayland: do not read one wrong run as a HexLinux
fault. xdotool picks keys from XWayland's keyboard map, which goes out of step
with the Windows one: accented characters came out wrong in one or two runs out
of three, with xdotool alone as with HexLinux, and once a first run typed
"42." as "$@<" (the shifted level of an AZERTY keyboard) before six runs in a
row came out right. Test Type with plain ASCII, repeat a failure once, and
compare with `xdotool type --file -` alone; the headless Sway below types
accents correctly.

`xterm` is not installed by default: `sudo apt-get install xterm`, or
`scripts/setup-dev.sh --test-tools`.

### The wlroots path, on a Sway without a screen

WSLg's compositor refuses wtype, but a Sway started without any display
accepts it, and gives wl-clipboard the data-control protocol GNOME lacks: the
path HexLinux takes on wlroots desktops can be exercised in WSL that way. It
was verified during the feasibility study (wtype and wl-clipboard both work
there); `scripts/setup-dev.sh --test-tools` installs `sway`, `foot` and `wev`.

```sh
export XDG_RUNTIME_DIR="$(mktemp -d)"      # a private one, mode 0700
unset WAYLAND_DISPLAY DISPLAY
printf 'output HEADLESS-1 resolution 1280x720\nxwayland disable\n' > /tmp/sway-test.conf
WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=pixman sway -c /tmp/sway-test.conf &
sleep 2
export WAYLAND_DISPLAY="$(basename "$(ls "$XDG_RUNTIME_DIR"/wayland-? | head -1)")"
stdbuf -oL wev &                           # prints the keys a window receives
./hexlinux --doctor                        # a wlroots session: wl-clipboard, wtype
./hexlinux --inject "hello" --mode Type --sender wtype
```

`wev` loses its last lines unless its output is line-buffered, hence `stdbuf`.
For a readable check, `foot sh -c 'cat > /tmp/out.txt'` gives a window whose
input lands in a file, as xterm does under X11.

## What WSL cannot test, and the evidence

Collected in this repository's WSL (Ubuntu 24.04.3, WSLg 1.0.65) on
2026-09-27. Each line below is the tool's own output.

**No keyboard devices.** The WSL kernel is built without the input event
interface, the keyboard drivers and the miscellaneous input drivers, uinput
among them:

```
$ uname -r
5.15.167.4-microsoft-standard-WSL2
$ zcat /proc/config.gz | grep -E '^(# )?CONFIG_INPUT_(EVDEV|UINPUT|KEYBOARD|MISC)\b'
# CONFIG_INPUT_EVDEV is not set
# CONFIG_INPUT_KEYBOARD is not set
# CONFIG_INPUT_MISC is not set
$ ls /dev/input /dev/uinput
ls: cannot access '/dev/input': No such file or directory
ls: cannot access '/dev/uinput': No such file or directory
```

`/proc/bus/input/devices` is empty. `--watch-hotkey` reports that no keyboard
is readable, and the daemon runs without the hotkey — `--toggle` still works.

This is the kernel of this WSL installation, and newer ones differ: the
configuration of Microsoft's 6.6 WSL kernel (`linux-msft-wsl-6.6.y` in
microsoft/WSL2-Linux-Kernel) has `CONFIG_INPUT_EVDEV=m` and
`CONFIG_INPUT_UINPUT=m`, and newer WSL releases ship it. After `wsl --update`,
HexLinux's virtual keyboard read back by its own keyboard reader might
therefore become testable in WSL — **not tried here**; `ls /dev/uinput` after
`sudo modprobe uinput` would tell. The physical keyboard would still not
appear: it reaches Linux applications through WSLg's RDP connection into its
compositor, never as a device (a deduction from WSLg's design).

**No virtual keyboard protocol, no tray, no notifications.** WSLg's compositor
is Weston with an RDP shell. The globals it offers, as `wayland-info` lists
them, contain no `zwp_virtual_keyboard_manager_v1` (wtype's protocol), no
data-control and no layer-shell:

```
weston_rdprail_shell  weston_screenshooter  wl_compositor  wl_data_device_manager
wl_output  wl_seat  wl_shell  wl_shm  wl_subcompositor  wp_presentation
wp_viewporter  xdg_wm_base  zwp_input_method_v1  zwp_input_panel_v1
zwp_input_timestamps_manager_v1  zwp_pointer_constraints_v1
zwp_relative_pointer_manager_v1  zwp_text_input_manager_v1
zxdg_output_manager_v1  zxdg_shell_v6
$ WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0 wtype -M shift -m shift
Compositor does not support the virtual keyboard protocol
```

The session bus exists (`unix:path=/run/user/0/bus`), but nothing on it
provides `org.freedesktop.Notifications` or `org.kde.StatusNotifierWatcher`:
there is no tray to show an icon, and no server to show a notification
(`notify-send` goes through the same service, and fails the same way). The
tray and menu code can still be exercised at the protocol level, on a private
bus with a stand-in watcher (`dbus-run-session`).

**No seat, never locked.** logind knows the WSL session, but as a text session
without a seat:

```
$ loginctl show-session 1 --property=Active --property=LockedHint --property=Type --property=Seat
Seat=
Type=tty
Active=yes
LockedHint=no
```

```
$ loginctl show-seat seat0 --property=ActiveSession
ActiveSession=
```

The session guard therefore sees an active, unlocked session, with nobody at
the seat, and allows insertion, which is right for WSL — and nothing there can
lock it, so the refusal path cannot be exercised. Without a seat, the
`uaccess` rule has no one to grant access to either.

**No real desktop.** No GNOME, KDE or wlroots compositor, no screen locker, no
login to autostart into; `xdotool getactivewindow` fails (WSLg's X server
publishes no `_NET_ACTIVE_WINDOW`), and `xdg-open` is not installed until you
add `xdg-utils`.

### What CI could add later

GitHub's Ubuntu runners have a kernel with uinput, and projects run
`sudo modprobe uinput` there to create virtual input devices and read them
back. A second, **non-required** job could use that for a loopback test of the
keyboard reader and the virtual keyboard, with Xvfb for xdotool and xclip,
`dbus-run-session` for the tray and a headless Sway for wtype. It does not exist
yet: the required check stays `Build, tests and coverage`, and none of this
replaces a real desktop.

## Testing on a virtual machine or real hardware

The target is **Ubuntu 24.04 LTS Desktop**: GNOME on Wayland by default, and
"Ubuntu on Xorg" one click away at the login screen — two sessions that insert
text in different ways, on one install. KDE Plasma and a wlroots compositor
come next: the Fedora KDE and Fedora Sway live images boot in the same virtual
machine without installing anything, and `sudo apt install sway` adds Sway to
Ubuntu's login screen.

### Which virtual machine

- **VMware Workstation Pro** is the one to start with. It has been free for
  every use since November 2024 (a Broadcom account is needed to download it),
  it runs on a Windows host where Hyper-V is active — which WSL2 makes it — and
  it leaves the right `Ctrl` key to the guest: the keyboard is released with
  `Ctrl`+`Alt`. Its virtual sound card passes the host's microphone through.
- **VirtualBox** works too, with three things to know. Its Host key is, by
  default, **the right `Ctrl` key** — HexLinux's default hotkey — which it keeps
  for itself, so the guest never sees it: change it before anything else,
  under File > Preferences > Input, "Host Key Combination" (the manual: "By
  default, this is the right Ctrl key on your keyboard"), or change `hotkey` in
  the guest's `settings.json`. Audio input has to be enabled in the machine's
  Audio settings. And on a host where Hyper-V is active, Oracle documents the
  combination as experimental, with a possible loss of performance.
- **Hyper-V**'s own virtual machines are not suited to the keyboard tests: the
  microphone only comes through the enhanced session, over RDP, and there the
  keyboard arrives through xrdp's X driver, not as a device HexLinux can read.
  Use them for the build, not for the keyboard.

On the Windows side, allow desktop applications to use the microphone
(Settings > Privacy & security > Microphone): the virtual machine is one.

### Setting it up, step by step

1. Download the Ubuntu 24.04 LTS Desktop image from
   <https://ubuntu.com/download/desktop>.
2. Create the machine: 4 processors, 8 GB of memory and 40 GB of disk are a
   comfortable size for GNOME plus the model, which holds about 1 GB of memory
   once it has transcribed a few dictations.
3. VirtualBox only: change the Host key, and enable audio input.
4. Install Ubuntu. Turn **copy and paste between host and guest off** while
   testing the clipboard (VMware: the machine's Guest Isolation options;
   VirtualBox: Shared Clipboard), or the host's clipboard joins in.
5. In the guest:

   ```sh
   sudo apt-get install -y git
   git clone https://github.com/legb78/hex_linux.git && cd hex_linux
   scripts/setup-dev.sh
   export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
   scripts/get-model.sh
   dotnet build -c Release
   cd src/HexLinux/bin/Release/net10.0
   ./hexlinux --doctor
   ```

   To test a release instead, extract `hexlinux-linux-x64.tar.gz`: it needs no
   SDK. For the autostart checks, run `scripts/publish.sh` and use the binary
   it builds (or the release): the output of `dotnet build` needs the SDK's
   runtime, which a login session does not find, and `--autostart on`
   refuses it.
6. Check the session type with `echo $XDG_SESSION_TYPE` (`wayland` on a fresh
   install), then take a **snapshot**, before any udev rule: it gives a clean
   state for the permission tests.
7. Run the checklist below in the GNOME session, log out, choose
   "Ubuntu on Xorg" with the gear at the bottom of the login screen, and run it
   again.

In a virtual machine the keyboard is emulated (an "AT Translated Set 2
keyboard" in `/proc/bus/input/devices`). Plugging and unplugging a real USB
keyboard, for the hot-plug checks, is done by passing it through in the
machine's USB settings.

### Real hardware

An **Ubuntu live USB** ("Try Ubuntu") gives GNOME on Wayland on your own
keyboard, microphone and screen, without installing anything: extract the
release archive there and run `./get-model.sh`. The live session keeps its
files in memory, so the model (about 490 MB to download, 670 MB extracted)
needs a machine with memory to spare, or a second stick. Autostart, the lock
screen and user switching are better tested on an installed system.

## First results on a real Ubuntu desktop

2026-09-30, HexLinux 0.1.0 (the release archive built from `develop` at
5402527), Ubuntu 24.04.5 LTS from the official cloud image with GNOME 46
(`ubuntu-desktop-minimal`), kernel 6.8.0-142-generic, in a VirtualBox 7.2.4
machine (4 processors, 8 GB) on a Windows 11 host with Hyper-V active, driven
over SSH. The keyboard was a test keyboard created through `/dev/uinput` —
the kernel's own evdev path, hot-plugged like a USB keyboard — and the
microphone a PipeWire null sink playing the test recording, made the default
source.

| Check | GNOME Wayland | GNOME Xorg |
|-------|---------------|------------|
| `--doctor` before the udev rule: keyboards unreadable, paste impossible, exit 3 | ✅ | — |
| `--watch-hotkey` before the rule: "permission denied", exit 5 | ✅ | — |
| `install-udev-rules.sh --with-uinput`: `getfacl` shows `user:<you>:rw-` on the keyboard and on `/dev/uinput`, with the user in no `input` group | ✅ | — |
| `--doctor` after the rule: paste through xclip and uinput (Wayland), xclip and xdotool (Xorg), exit 0 | ✅ | ✅ |
| `--transcribe` of the test recording | ✅ | ✅ |
| Hold right `Ctrl` on the hot-plugged keyboard, speak, release: the text lands in a native GTK4 window | ✅ | ✅ |
| The previous clipboard content comes back after the paste | ✅ | ✅ |
| Type mode, typed by xdotool | impossible, as documented | ✅ |
| A quick right `Ctrl`+`C`: no tone, no dictation | ✅ | — |
| Screen locked (`loginctl lock-session`): the hotkey and `--toggle` are refused, nothing inserted, "dictation refused: session … is locked" | ✅ | — |
| Tray icon shown by the AppIndicator extension; tooltip "HexLinux — ready (Right Ctrl)"; menu entries and their check marks; "Dictate now" then "Finish dictation" dictate | ✅ | ✅ |
| The icon registers again after the lock screen, and at login once the extension is up | ✅ | ✅ |
| `--autostart on`: `desktop-file-validate` passes; after a new login the daemon is running; `--autostart off` removes the entry | — | ✅ |

Speed in that virtual machine, for the record only: the model loaded in 7 to
12 s and the 5 s recording was transcribed in about 2 s, against 0.2 s under
WSL on the same host. VirtualBox beside Hyper-V is slow; these are not
figures for native Linux.

Still to be done by a person, at the machine: a real voice into a real
microphone, a physical press of the hotkey (change VirtualBox's Host key
first), KDE Plasma and Sway, a user switch, and a clipboard history manager.

## Manual checklist

Run `./hexlinux --doctor` first in every session, and keep its output with the
results: it states what HexLinux saw, which is what the results have to be read
against. The expected plans below follow the insertion rules; `--doctor` prints
the actual one.

### Per session: what insertion should use

| Session | Clipboard | Paste shortcut sent by | Type mode |
|---------|-----------|------------------------|-----------|
| GNOME, Wayland | xclip, through XWayland | uinput, with the optional rule | none for Wayland windows; `clipboardFallback` if enabled |
| GNOME, Xorg | xclip | xdotool | xdotool |
| KDE Plasma, Wayland | wl-clipboard | uinput, with the optional rule | none (KWin offers no virtual keyboard protocol) |
| KDE Plasma, X11 | xclip | xdotool | xdotool |
| Sway, Hyprland (wlroots) | wl-clipboard | uinput if allowed, otherwise wtype | wtype |
| Xfce, Cinnamon, MATE (X11) | xclip | xdotool | xdotool |

The GNOME Wayland row is confirmed (see [First results](#first-results-on-a-real-ubuntu-desktop)):
Mutter keeps the X11 and Wayland clipboards in step, and a dictation put in
the X11 clipboard by xclip is pasted into a native Wayland window by the
virtual keyboard's Ctrl+V. That `wl-copy` would steal the focus there is still
unobserved: HexLinux does not use it on GNOME. Record what you see on other
desktops.

### Keyboard (evdev)

| Check | How | Expected |
|-------|-----|----------|
| Hotkey without permission | before installing the udev rule: `./hexlinux --watch-hotkey`, then the daemon | one "permission denied" line, shown by `--doctor` too; the daemon keeps running and `--toggle` works |
| Hotkey with permission | `sudo ./install-udev-rules.sh`, log out and in, `./hexlinux --watch-hotkey`, hold right `Ctrl` | `start` on press, `stop` on release; letters and digits are never printed |
| The permission itself | `getfacl /dev/input/event*` for the keyboards | your user has an ACL entry, after the install and again after logging out and in |
| Foreign key cancels | hold right `Ctrl`, press a letter | `cancel`, nothing inserted |
| A shortcut typed right after a dictation | dictate a long sentence, release, press right `Ctrl`+`C` at once | the dictation is inserted; the copy happens as usual |
| The shortcut during a `--toggle` dictation | `./hexlinux --toggle`, speak, press and release right `Ctrl`, then right `Ctrl`+`V` | the dictation goes on; the next `--toggle` ends it |
| A quick right `Ctrl`+letter | right `Ctrl`+`C`, quickly | no tone, nothing in the log: a shortcut, not a dictation |
| Auto-repeat | hold right `Ctrl` for several seconds | one `start`, one `stop` |
| Two-key shortcut released out of order | `"hotkey": ["Ctrl", "Super"]`, release Super first | text intact, no system shortcut triggered |
| Keyboard unplugged, key held | USB keyboard, hold the hotkey, unplug it | the dictation is cancelled; after plugging it back, the hotkey works |
| Hot-plug | plug a second keyboard while the daemon runs | its keys are seen within a few seconds |

### Virtual keyboard, insertion and clipboard

| Check | How | Expected |
|-------|-----|----------|
| Virtual keyboard lifetime | `sudo ./install-udev-rules.sh --with-uinput`, log out and in, start the daemon, read `/proc/bus/input/devices` | "hexlinux virtual keyboard" is listed while the daemon runs, gone after it stops |
| Paste through uinput | GNOME Wayland: `./hexlinux --inject "x" --sender uinput` into a text editor | `x` pasted |
| Non-QWERTY-position layout | the same with Dvorak or Bépo | expected to fail: uinput sends the key position, and V is elsewhere on those layouts; `ShiftInsert` is the workaround |
| Type mode | `--mode Type` in each session | typed where the table above says it can be, a readable problem elsewhere |
| wtype | Sway: `./hexlinux --inject "x" --sender wtype` | typed; on GNOME or KDE, a readable problem |
| Clipboard restored | copy an image, dictate into an editor, paste elsewhere | the image comes back |
| Nothing to restore | clear the clipboard, dictate, paste elsewhere | nothing: the dictation is not left behind |
| Clipboard history | KDE (Klipper) or a GNOME history extension, dictate in Paste mode, then in Type mode | the Paste dictation appears in the history, the Type one does not (documented in SECURITY.md) |
| Fallback | GNOME Wayland without the uinput rule, `"clipboardFallback": true` | a notification asks you to press Ctrl+V; the text is in the clipboard |
| Shift+Insert in xterm | `"pasteShortcut": "ShiftInsert"`, dictate into xterm | xterm pastes the primary selection, not the dictation (documented) |
| Terminal | `"pasteShortcut": "CtrlShiftV"`, dictate into GNOME Terminal | the dictation is pasted |

### Session guard

| Check | How | Expected |
|-------|-----|----------|
| Locked screen | lock the screen, then trigger `--toggle` from a timer (`sleep 10; ./hexlinux --toggle` started before locking) or over SSH | nothing inserted; a "dictation refused" log line |
| Locked during a dictation | `./hexlinux --toggle`, speak, lock the screen | the recording stops within a couple of seconds ("dictation stopped: session … is locked" in the log); nothing inserted |
| Shortcut on the lock screen | hold right `Ctrl` on the lock screen (hotkey installed) | no tone, nothing recorded |
| Locker without LockedHint | Sway with swaylock, the same test | expected to insert: swaylock does not tell logind (documented limit); record the result |
| User switch | switch to another user, trigger the hotkey there | nothing inserted into the other session; the first session's log says its keyboards were closed |
| Daemon started from SSH | user A starts the daemon over SSH with no graphical session, user B logs in at the machine | A's daemon refuses: "another user's session (…) is in front of the screen" |

### Microphone, engine, tones

| Check | How | Expected |
|-------|-----|----------|
| Microphone | `./hexlinux --record test.wav` | duration and a level above silence |
| Muted microphone | mute it, the same | "silent" reported, exit code 4 |
| Engine | `./hexlinux --transcribe test.wav` | what you said |
| Speed | `--record` 5 s and 40 s of speech (`--seconds 40`), then `--transcribe` each | the time taken, printed; under WSL2 on a Core Ultra 7 255H it was 0.215 s and 1.616 s (4 threads) — record the figures of the machine you test on, native figures are still missing from the README |
| Tones | `./hexlinux --test-feedback` | a high tone, then a lower one |
| PipeWire | the same on a PipeWire system (Ubuntu 24.04 is one) | identical: pipewire-pulse serves the same interface |

### Tray, menu, notifications

| Check | How | Expected |
|-------|-----|----------|
| Icon states | KDE, and GNOME with the AppIndicator extension (Ubuntu ships it on): dictate | Idle → Recording → Transcribing → Idle; tooltip "HexLinux — ready (Right Ctrl)" |
| No tray host | GNOME without the extension | no icon; the daemon runs, a log line says so |
| Menu | each entry | "Dictate now" dictates, and reads "Finish dictation" while recording; "Open settings file" and "Open log folder" open them; "Play tones" switches `feedback` in `settings.json` and nothing else; "Start at login" creates or removes the entry; "Quit" stops the daemon — during a transcription too, after it, without a crash |
| Notifications | remove the model, then start | a "model not found" notification naming `get-model.sh` |

### Autostart and permissions

| Check | How | Expected |
|-------|-----|----------|
| Autostart | from the `scripts/publish.sh` output (or the release): `./hexlinux --autostart on`, `desktop-file-validate ~/.config/autostart/hexlinux.desktop`, log out and in, `./hexlinux --status` | no validation error; the daemon is running |
| Autostart from a `dotnet build` output | `src/HexLinux/bin/Release/net10.0/hexlinux --autostart on` | refused, pointing at `scripts/publish.sh` |
| Autostart, another copy run once | with the entry pointing at the published binary, start the `dotnet build` one once | the entry still points at the published binary |
| Autostart off | `./hexlinux --autostart off` | the entry is gone |
| udev rule install | `sudo ./install-udev-rules.sh`, log out and in, `./hexlinux --doctor` | keyboards readable (and uinput with `--with-uinput`) |
| udev rule removal | `sudo ./install-udev-rules.sh --uninstall` | no HexLinux file left under `/etc/udev/rules.d`; after logging out and in, `--doctor` shows the keyboards unreadable again |

Report what you ran and what you saw in the pull request, one line per
session: the table in the pull request template is there for that.
