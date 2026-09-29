#!/usr/bin/env bash
#
# Downloads the speech recognition model to where HexLinux looks for it.
#
#   get-model.sh                       # Parakeet TDT 0.6B v3, 25 European languages
#   get-model.sh --model parakeet-v2   # English only, marginally more accurate on it
#   get-model.sh --force               # download again even if already present
#   get-model.sh --dest DIR            # another models folder
#
# The default folder is ${XDG_DATA_HOME:-~/.local/share}/hexlinux/models: the
# first place HexLinux searches, and one only the user can write to. The same
# folder is used whether the script runs from a clone or from the release
# archive, so it never depends on where it was unpacked.
#
# Models are not in the repository: they weigh several hundred megabytes, far
# past GitHub's file size limit.
#
# What makes it safe to run: the archive goes to an unpredictable temporary
# file next to the destination, its size and SHA-256 are compared with the
# values pinned below BEFORE anything is extracted, only the four files the
# engine needs are extracted, and the finished folder is moved into place in
# one rename. A dropped connection, a corrupt download or an archive changed
# upstream therefore installs nothing and leaves an existing model untouched.

set -euo pipefail

usage() {
  sed -n '3,23p' "$0" | sed 's/^# \{0,1\}//'
}

model="parakeet-v3"
force=0
dest_parent="${XDG_DATA_HOME:-$HOME/.local/share}/hexlinux/models"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --model)
      [ "$#" -ge 2 ] || { echo "--model needs a value: parakeet-v3 or parakeet-v2." >&2; exit 64; }
      model="$2"
      shift 2
      ;;
    --dest)
      [ "$#" -ge 2 ] || { echo "--dest needs a folder." >&2; exit 64; }
      dest_parent="$2"
      shift 2
      ;;
    --force)
      force=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1 (see --help)" >&2
      exit 64
      ;;
  esac
done

# Size and SHA-256 of each archive, as published by the GitHub API for the
# asr-models release of k2-fsa/sherpa-onnx (both uploaded 2025-08-16), and
# recomputed from a full download for v3. That release is a rolling one whose
# assets can be republished: a mismatch then fails closed, and the values here
# have to be updated in a pull request after checking the new archive.
case "$model" in
  parakeet-v3)
    name="sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
    size=487170055
    sha256=5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf
    ;;
  parakeet-v2)
    name="sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"
    size=482468385
    sha256=157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad
    ;;
  *)
    echo "Unknown model: $model (parakeet-v3 or parakeet-v2)." >&2
    exit 64
    ;;
esac

url="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2"
destination="$dest_parent/$name"

# The files the engine requires, with the smallest size each may have: the
# same floors HexLinux checks before loading (src/HexLinux/Transcription/ModelFiles.cs).
required=(encoder.int8.onnx decoder.int8.onnx joiner.int8.onnx tokens.txt)
minimum=($((50 * 1024 * 1024)) $((100 * 1024)) $((100 * 1024)) 1024)

# True when every required file is there, a regular file, and not truncated.
complete() {
  local folder="$1" i file
  for i in "${!required[@]}"; do
    file="$folder/${required[$i]}"
    if [ ! -f "$file" ] || [ -L "$file" ] || [ "$(stat -c %s -- "$file")" -lt "${minimum[$i]}" ]; then
      return 1
    fi
  done
}

# Each tool, with the Debian/Ubuntu package that provides it. tar needs the
# bzip2 program itself to read a .tar.bz2.
for tool in curl:curl tar:tar bzip2:bzip2 sha256sum:coreutils stat:coreutils df:coreutils; do
  if ! command -v "${tool%%:*}" >/dev/null 2>&1; then
    echo "${tool%%:*} is missing: install the ${tool##*:} package (sudo apt-get install ${tool##*:})." >&2
    exit 1
  fi
done

echo "Model       : $name"
echo "Destination : $destination"

if [ "$(id -u)" -eq 0 ]; then
  echo "Warning: running as root, so the model goes to root's folder ($dest_parent)." >&2
  echo "         Run this as the user who dictates unless that is what you want." >&2
fi

if [ -e "$destination" ] && [ "$force" -eq 0 ]; then
  if complete "$destination"; then
    echo "Already present and complete, nothing to do."
    echo "Use --force to download again."
    exit 0
  fi
  echo "Present but incomplete: downloading again." >&2
fi

mkdir -p -- "$dest_parent"

# Room for the archive and what it extracts to, with some margin: for v3,
# 487 MB of archive become 670 MB of model.
needed_kb=$((size * 5 / 2 / 1024))
available_kb="$(df -Pk -- "$dest_parent" | awk 'NR == 2 { print $4 }')"
if [ -n "$available_kb" ] && [ "$available_kb" -lt "$needed_kb" ]; then
  echo "Not enough free space in $dest_parent: about $((needed_kb / 1024)) MB needed, $((available_kb / 1024)) MB available." >&2
  exit 1
fi

# Same file system as the destination, so the final move is a rename: a reader
# never sees a half-copied model. mktemp's name cannot be guessed in advance.
work="$(mktemp -d -- "$dest_parent/.get-model.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# HTTPS only, redirections included: curl would otherwise follow a redirect to
# plain HTTP. A progress bar only when somebody is watching.
progress=(--silent --show-error)
if [ -t 2 ]; then
  progress=(--progress-bar)
fi

echo "Archive     : $((size / 1024 / 1024)) MB to download"
curl --fail --location --proto '=https' --proto-redir '=https' --retry 3 \
  "${progress[@]}" --output "$work/archive" -- "$url"

actual_size="$(stat -c %s -- "$work/archive")"
if [ "$actual_size" != "$size" ]; then
  echo "Size mismatch: $actual_size bytes received, $size expected. Nothing was installed." >&2
  exit 1
fi

echo "Checking the SHA-256..."
if ! printf '%s  %s\n' "$sha256" "$work/archive" | sha256sum --check --status; then
  echo "SHA-256 mismatch: the upstream archive changed or the download is corrupt. Nothing was installed." >&2
  exit 1
fi

echo "Extracting..."
members=()
for file in "${required[@]}"; do
  members+=("$name/$file")
done

# Only the four files, owned by whoever runs this rather than by the uid
# recorded in the archive, and with the usual permissions rather than the
# archive's. The archive's content is known at this point: its hash matched.
tar -xjf "$work/archive" -C "$work" --no-same-owner --no-same-permissions -- "${members[@]}"
rm -f -- "$work/archive"

if ! complete "$work/$name"; then
  echo "The archive was extracted but a file is missing or truncated. Nothing was installed." >&2
  exit 1
fi

if [ -e "$destination" ]; then
  rm -rf -- "$destination.old"
  mv -- "$destination" "$destination.old"
fi
mv -- "$work/$name" "$destination"
rm -rf -- "$destination.old"

echo ""
echo "Model installed."
echo ""
echo "Check the transcription chain with:"
echo "  hexlinux --doctor"
echo "  hexlinux --transcribe my-recording.wav"
