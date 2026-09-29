#!/usr/bin/env bash
#
# Installs the udev rules that let HexLinux see the dictation shortcut held.
#
#   sudo ./install-udev-rules.sh                  # keyboards readable
#   sudo ./install-udev-rules.sh --with-uinput    # also the virtual keyboard
#   sudo ./install-udev-rules.sh --uninstall      # removes everything it installed
#        ./install-udev-rules.sh --destdir DIR    # stage the files under DIR, no root
#
# Keyboards: /etc/udev/rules.d/70-hexlinux.rules. With --with-uinput, also
# /etc/udev/rules.d/70-hexlinux-uinput.rules and /etc/modules-load.d/hexlinux.conf:
# needed only to paste under GNOME or KDE on Wayland.
#
# Read what each rule grants before installing it: the files explain it, and
# this script prints them. In short, the keyboard rule lets every program of
# the user at the machine read and inject keystrokes; the uinput rule lets
# them type anywhere. Without either, `hexlinux --toggle` bound to a desktop
# shortcut still dictates. The "input" group is never used: it would grant the
# same, permanently, in every session.
#
# Run it again at any time: installing twice changes nothing.

set -euo pipefail

usage() {
  sed -n '3,21p' "$0" | sed 's/^# \{0,1\}//'
}

with_uinput=0
uninstall=0
destdir=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --with-uinput) with_uinput=1; shift ;;
    --uninstall) uninstall=1; shift ;;
    --destdir)
      [ "$#" -ge 2 ] || { echo "--destdir needs a folder." >&2; exit 64; }
      destdir="${2%/}"
      shift 2
      ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 64 ;;
  esac
done

if [ "$with_uinput" -eq 1 ] && [ "$uninstall" -eq 1 ]; then
  echo "--with-uinput and --uninstall do not go together: --uninstall removes both rules." >&2
  exit 64
fi

# The only lines a rule file may carry besides comments. Checked before a file
# is installed, so that what lands in /etc as root is exactly this, whatever
# happened to the copy beside the script: a udev rule can run programs as root.
keyboard_rule='SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_KEYBOARD}=="1", TAG+="uaccess"'
uinput_rule='KERNEL=="uinput", SUBSYSTEM=="misc", TAG+="uaccess", OPTIONS+="static_node=uinput"'
module_line='uinput'

rules_dir="$destdir/etc/udev/rules.d"
modules_dir="$destdir/etc/modules-load.d"
keyboard_target="$rules_dir/70-hexlinux.rules"
uinput_target="$rules_dir/70-hexlinux-uinput.rules"
module_target="$modules_dir/hexlinux.conf"

if [ -z "$destdir" ] && [ "$(id -u)" -ne 0 ]; then
  echo "This installs files under /etc: run it with sudo." >&2
  exit 1
fi

# Beside the script in the release archive, one level up in a clone.
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
packaging=""
for candidate in "$here/packaging" "$here/../packaging"; do
  if [ -f "$candidate/udev/70-hexlinux.rules" ]; then
    packaging="$(cd -- "$candidate" && pwd)"
    break
  fi
done

# Tells udev about the change and applies it to the devices already there, so
# that the access follows without a reboot where the desktop allows it. Skipped
# when staging, and never fatal: without a running udev (a container), the
# files still take effect at the next boot.
reload() {
  if [ -n "$destdir" ]; then
    return
  fi
  if ! command -v udevadm >/dev/null 2>&1; then
    echo "udevadm not found: the rules take effect at the next boot." >&2
    return
  fi
  if udevadm control --reload; then
    echo "udev rules reloaded."
  else
    echo "Could not reload udev: the rules take effect at the next boot." >&2
    return
  fi
  udevadm trigger --action=change --subsystem-match=input || true
  if [ -e /dev/uinput ]; then
    udevadm trigger --action=change --name-match=uinput || true
  fi
}

remove() {
  local file="$1"
  if [ -e "$file" ] || [ -L "$file" ]; then
    rm -f -- "$file"
    echo "Removed $file"
  fi
}

if [ "$uninstall" -eq 1 ]; then
  remove "$keyboard_target"
  remove "$uinput_target"
  remove "$module_target"
  reload
  echo ""
  echo "Done. Access already granted may last until you log out and back in,"
  echo "or until the next reboot."
  exit 0
fi

if [ -z "$packaging" ]; then
  echo "packaging/udev/70-hexlinux.rules not found next to this script or one level up." >&2
  exit 1
fi

# Refuses a file whose active lines are not exactly the expected one.
check() {
  local file="$1" expected="$2" active
  if [ ! -f "$file" ] || [ -L "$file" ]; then
    echo "$file is missing or is not a regular file." >&2
    exit 1
  fi
  active="$(grep -vE '^[[:space:]]*(#|$)' -- "$file" || true)"
  if [ "$active" != "$expected" ]; then
    echo "$file does not hold the expected rule; refusing to install it. Its active lines:" >&2
    printf '%s\n' "$active" >&2
    exit 1
  fi
}

# Shows the file, then installs it root-owned and read-only for others.
put() {
  local source="$1" target="$2"
  echo ""
  echo "--- $target"
  cat -- "$source"
  echo "---"
  if [ -n "$destdir" ]; then
    install -D -m 0644 -- "$source" "$target"
  else
    install -D -m 0644 -o root -g root -- "$source" "$target"
  fi
  echo "Installed $target"
}

check "$packaging/udev/70-hexlinux.rules" "$keyboard_rule"
put "$packaging/udev/70-hexlinux.rules" "$keyboard_target"

if [ "$with_uinput" -eq 1 ]; then
  check "$packaging/udev/70-hexlinux-uinput.rules" "$uinput_rule"
  check "$packaging/modules-load.d/hexlinux.conf" "$module_line"
  put "$packaging/udev/70-hexlinux-uinput.rules" "$uinput_target"
  put "$packaging/modules-load.d/hexlinux.conf" "$module_target"

  if [ -z "$destdir" ] && [ ! -e /dev/uinput ]; then
    if command -v modprobe >/dev/null 2>&1 && modprobe uinput; then
      echo "uinput driver loaded."
    else
      echo "Could not load the uinput driver now. If this kernel ships it as a module," >&2
      echo "it loads at the next boot; if it has no uinput at all, pasting under" >&2
      echo "GNOME or KDE on Wayland stays unavailable (hexlinux --doctor says so)." >&2
    fi
  fi
elif [ -e "$uinput_target" ]; then
  echo ""
  echo "Note: $uinput_target is installed from an earlier run and stays;"
  echo "      --uninstall removes it."
fi

reload

cat <<'EOF'

Done. Log out and back in (or reboot) if `hexlinux --doctor` does not show the
keyboards as readable yet. To undo: sudo ./install-udev-rules.sh --uninstall
EOF
