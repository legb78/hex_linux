#!/usr/bin/env bash
#
# Installs what building and testing HexLinux needs, on Debian or Ubuntu
# (WSL included).
#
#   scripts/setup-dev.sh                # SDK and runtime tools
#   scripts/setup-dev.sh --test-tools   # the same, plus the manual-test tools
#   scripts/setup-dev.sh --no-apt       # the .NET SDK only, no system packages
#
# The .NET SDK goes to ~/.dotnet through Microsoft's dotnet-install.sh, the
# same version on every distribution, rather than whatever the distribution
# happens to package. The system packages are the tools HexLinux drives at run
# time; none of them is needed to build or to run the unit tests. Running the
# script again changes nothing that is already in place.

set -euo pipefail

channel="10.0"
with_apt=1
with_test_tools=0

for argument in "$@"; do
  case "$argument" in
    --no-apt) with_apt=0 ;;
    --test-tools) with_test_tools=1 ;;
    -h|--help)
      sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Unknown option: $argument" >&2
      exit 64
      ;;
  esac
done

if [ "$with_apt" -eq 0 ] && [ "$with_test_tools" -eq 1 ]; then
  echo "--test-tools installs system packages: it cannot go with --no-apt." >&2
  exit 64
fi

if [ "$with_apt" -eq 1 ]; then
  if ! command -v apt-get >/dev/null 2>&1; then
    echo "apt-get not found: install the equivalent packages by hand (see docs/testing.md)." >&2
    exit 1
  fi

  sudo_cmd=""
  if [ "$(id -u)" -ne 0 ]; then
    sudo_cmd="sudo"
  fi

  # bzip2            : get-model.sh extracts a .tar.bz2
  # libpulse0        : microphone and tones (libpulse-simple), PipeWire included
  # pulseaudio-utils : pactl, to check which microphone is the default
  # wl-clipboard     : clipboard under Wayland
  # xclip            : clipboard under X11 and XWayland
  # xdotool          : keystrokes under X11
  # wtype            : keystrokes under wlroots compositors (Sway, Hyprland)
  # libnotify-bin    : notify-send, for notifications when the session bus
  #                    cannot be reached directly
  # xdg-utils        : xdg-open, behind "Open settings file" and "Open log folder"
  packages=(
    ca-certificates curl bzip2
    libpulse0 pulseaudio-utils
    wl-clipboard xclip xdotool wtype
    libnotify-bin xdg-utils
  )

  # Only for the checks of docs/testing.md and for reviewing the scripts: an
  # X11 window whose input can be read back (xterm), a linter for the shell
  # scripts (shellcheck), desktop-file-validate for the autostart entry
  # (desktop-file-utils), parsing the workflows (python3-yaml), wayland-info
  # to see what a compositor offers (wayland-utils), dbus-run-session for a
  # private bus in the tray tests (dbus-daemon), and a Sway to run without a
  # screen, with a terminal and an event viewer for it (sway, foot, wev).
  if [ "$with_test_tools" -eq 1 ]; then
    packages+=(xterm shellcheck desktop-file-utils python3-yaml wayland-utils dbus-daemon sway foot wev)
  fi

  $sudo_cmd apt-get update
  $sudo_cmd apt-get install -y --no-install-recommends "${packages[@]}"
fi

install_dir="$HOME/.dotnet"

if [ -x "$install_dir/dotnet" ] && "$install_dir/dotnet" --list-sdks | grep -q "^${channel}\."; then
  echo ".NET SDK ${channel} already installed in $install_dir."
else
  installer="$(mktemp)"
  trap 'rm -f "$installer"' EXIT

  # HTTPS only, redirects included: curl would otherwise follow one to plain
  # HTTP, and the script it fetches runs right after.
  curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' https://dot.net/v1/dotnet-install.sh --output "$installer"
  bash "$installer" --channel "$channel" --install-dir "$install_dir"
fi

cat <<EOF

Done. Add the SDK to your shell if it is not there yet:

  export DOTNET_ROOT="\$HOME/.dotnet"
  export PATH="\$DOTNET_ROOT:\$PATH"

Then:

  dotnet build -c Release
  dotnet test
EOF

# In WSL with systemd enabled, WAYLAND_DISPLAY names a socket that logind's
# own /run/user/<uid> hides; WSLg's real one is under /mnt/wslg.
wslg_socket=/mnt/wslg/runtime-dir/wayland-0
if [ -S "$wslg_socket" ] && [ -n "${WAYLAND_DISPLAY:-}" ] && [ "${WAYLAND_DISPLAY#/}" = "$WAYLAND_DISPLAY" ] \
  && [ ! -S "${XDG_RUNTIME_DIR:-/nonexistent}/$WAYLAND_DISPLAY" ]; then
  cat <<EOF

WSL: Wayland clients cannot find their socket in this shell. Before testing
anything that uses Wayland (wl-clipboard, wtype), point them at WSLg's:

  export WAYLAND_DISPLAY=$wslg_socket
EOF
fi
