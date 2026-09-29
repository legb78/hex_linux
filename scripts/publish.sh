#!/usr/bin/env bash
#
# Builds the self-contained HexLinux executable.
#
#   scripts/publish.sh                  # publish/hexlinux and publish/settings.json
#   scripts/publish.sh --output DIR     # somewhere else
#   scripts/publish.sh --archive        # also hexlinux-linux-x64.tar.gz, as released
#
# A single file: neither .NET nor any dependency has to be installed on the
# target machine. The sherpa-onnx native libraries are embedded in it and
# extracted on first run. The model is not included; get-model.sh downloads it.
#
# --archive assembles the same tarball as the release workflow, in the output
# folder: a tarball rather than a zip because tar keeps the executable bits.
# A missing file fails it rather than produce an archive with a hole in it.

set -euo pipefail

usage() {
  sed -n '3,15p' "$0" | sed 's/^# \{0,1\}//'
}

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/publish"
archive=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --output)
      [ "$#" -ge 2 ] || { echo "--output needs a folder." >&2; exit 64; }
      output="$2"
      shift 2
      ;;
    --archive) archive=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 64 ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found: run scripts/setup-dev.sh, then add ~/.dotnet to PATH." >&2
  exit 1
fi

echo "Destination : $output"
echo ""

dotnet publish "$root/src/HexLinux/HexLinux.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o "$output"

executable="$output/hexlinux"
echo ""
echo "Executable built: $executable ($(( $(stat -c %s -- "$executable") / 1024 / 1024 )) MB)"

if [ "$archive" -eq 1 ]; then
  staging="$(mktemp -d)"
  trap 'rm -rf -- "$staging"' EXIT
  package="$staging/hexlinux"

  mkdir -p -- "$package/packaging/udev" "$package/packaging/modules-load.d"
  install -m 0755 -- "$executable" "$package/"
  install -m 0644 -- "$output/settings.json" "$package/"
  install -m 0755 -- "$root/scripts/get-model.sh" "$root/scripts/install-udev-rules.sh" "$package/"
  install -m 0644 -- "$root/packaging/udev/70-hexlinux.rules" "$root/packaging/udev/70-hexlinux-uinput.rules" "$package/packaging/udev/"
  install -m 0644 -- "$root/packaging/modules-load.d/hexlinux.conf" "$package/packaging/modules-load.d/"
  install -m 0644 -- "$root/README.md" "$root/LICENSE" "$root/NOTICE" "$package/"

  # Entries recorded as owned by root, by number, as the release workflow
  # does: extracted by root (to install the udev rules), the files would
  # otherwise belong to whatever uid built the archive.
  tarball="$output/hexlinux-linux-x64.tar.gz"
  tar -czf "$tarball" --owner=0 --group=0 --numeric-owner -C "$staging" hexlinux
  echo ""
  tar -tvzf "$tarball"
  echo ""
  echo "Archive built: $tarball"
fi

echo ""
echo "The model is not included. On the target machine:"
echo "  ./get-model.sh"
