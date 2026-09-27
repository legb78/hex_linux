#!/usr/bin/env bash
#
# Installs what building and testing HexLinux needs, on Debian or Ubuntu
# (WSL included).
#
#   scripts/setup-dev.sh            # SDK and runtime tools
#   scripts/setup-dev.sh --no-apt   # the .NET SDK only, no system packages
#
# The .NET SDK goes to ~/.dotnet through Microsoft's dotnet-install.sh, the
# same version on every distribution, rather than whatever the distribution
# happens to package. The system packages are the tools HexLinux drives at run
# time; none of them is needed to build or to run the unit tests.

set -euo pipefail

channel="10.0"
with_apt=1

for argument in "$@"; do
  case "$argument" in
    --no-apt) with_apt=0 ;;
    -h|--help)
      sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Unknown option: $argument" >&2
      exit 64
      ;;
  esac
done

if [ "$with_apt" -eq 1 ]; then
  if ! command -v apt-get >/dev/null 2>&1; then
    echo "apt-get not found: install the equivalent packages by hand (see docs/testing.md)." >&2
    exit 1
  fi

  sudo_cmd=""
  if [ "$(id -u)" -ne 0 ]; then
    sudo_cmd="sudo"
  fi

  # bzip2          : get-model.sh extracts a .tar.bz2
  # libpulse0      : microphone and tones (libpulse-simple), PipeWire included
  # pulseaudio-utils: pactl, to check which microphone is the default
  # wl-clipboard   : clipboard under Wayland
  # xclip          : clipboard under X11 and XWayland
  # xdotool        : keystrokes under X11
  # wtype          : keystrokes under wlroots compositors (Sway, Hyprland)
  # libnotify-bin  : notify-send, for the error notifications
  $sudo_cmd apt-get update
  $sudo_cmd apt-get install -y --no-install-recommends \
    ca-certificates curl bzip2 \
    libpulse0 pulseaudio-utils \
    wl-clipboard xclip xdotool wtype \
    libnotify-bin
fi

install_dir="$HOME/.dotnet"

if [ -x "$install_dir/dotnet" ] && "$install_dir/dotnet" --list-sdks | grep -q "^${channel}\."; then
  echo ".NET SDK ${channel} already installed in $install_dir."
else
  installer="$(mktemp)"
  trap 'rm -f "$installer"' EXIT

  curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh --output "$installer"
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
