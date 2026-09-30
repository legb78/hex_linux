## What

<!-- What this pull request does, in a sentence or two. -->

## Why

<!-- The problem it solves or the need it covers. -->

## Verified

- [ ] Title in [Conventional Commits](https://www.conventionalcommits.org/), in English — the squash takes it
- [ ] `dotnet build -c Release` — 0 warnings
- [ ] `dotnet format --verify-no-changes` — clean
- [ ] `dotnet test` — green
- [ ] Changes to `.github/`, `scripts/`, `packaging/`, a `.csproj` or
      `Directory.Build.props` read line by line — a pull request runs its own
      workflow, so a green check cannot vouch for them
- [ ] Manual check (fill in the table below if this touches the keyboard, the
      microphone, text insertion, the clipboard, the tray or the notifications
      — those layers cannot be tested automatically, and the keyboard cannot be
      tested in WSL at all: see `docs/testing.md`)

<!--
One row per desktop and session tried. The session type matters as much as
the desktop: GNOME on Wayland and GNOME on Xorg insert text differently.

Useful commands:
  hexlinux --doctor                what HexLinux sees of the session
  hexlinux --watch-hotkey          keyboard
  hexlinux --record test.wav       microphone
  hexlinux --transcribe test.wav   engine
  hexlinux --inject "some text"    insertion; add --mode Type or --sender ...
  hexlinux --test-feedback         tones
  hexlinux --toggle                dictation through the control socket
-->

| Layer | Distribution, desktop, session | What was tried | Result |
|-------|--------------------------------|----------------|--------|
|       |                                |                |        |
