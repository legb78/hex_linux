#!/usr/bin/env bash
#
# Checks commit subjects and pull request titles against Conventional Commits.
#
#   scripts/check-conventional-commits.sh
#       In CI. EVENT_NAME, PR_TITLE, BASE_SHA and HEAD_SHA come from the
#       environment; what is checked depends on the event, see below.
#   scripts/check-conventional-commits.sh --range origin/develop..HEAD
#       Before opening a pull request: every commit of the current branch.
#   printf '%s\n' 'feat: add x' 'Fix typo' | scripts/check-conventional-commits.sh --stdin
#       Any list of subjects, one per line.
#
# Exit status: 0 everything that counts conforms, 1 something does not,
# 64 wrong usage, 65 the commits could not be listed.
#
# What is checked, and why. The subject that lands on develop is the one to
# get right, and a squash merge takes it from the pull request title, or from
# the commit when the pull request has only one (COMMIT_OR_PR_TITLE). So, on a
# pull request:
#   - the title, always;
#   - the subject of its commit, when it has only one;
#   - the subjects of its other commits are reported, but do not fail the
#     check: a squash only lists them in the body. A rebase merge would keep
#     them as they are, though, which the notes say.
# On a push (to develop, after a merge): every commit the push brought, since
# they have now landed — a squash title edited by hand in the merge dialog
# shows up there, as would the commits of a rebase merge.
#
# Merge commits are left out. The branch rules require a pull request to be up
# to date with its base before it merges, so "Update branch" is routine, and
# that button writes "Merge branch 'develop' into ..." — a subject nobody
# chose. Whether GitHub counts such a merge when it decides between the commit
# subject and the title is not documented, so a pull request made of one
# commit plus merges has both that subject and its title checked: whichever
# the squash takes, it has been verified.
#
# In GitHub Actions, the output runs with the runner's workflow commands
# stopped (::stop-commands:: and a random token): the runner still honours an
# old "##[command]" form anywhere in a line, and a title or a subject is text
# anyone can write. They are resumed for this script's own annotations only.
#
# Why this exists at all: the Windows sibling of this project follows the same
# convention by hand, and a pull request titled after its branch, "Feat/recording
# feedback", reached its develop branch and then main that way. Nothing had
# said no.
#
# What is accepted:
#
#   <type>(<scope>)!: <description>
#
#   type         build, chore, ci, docs, feat, fix, perf, refactor, revert,
#                style or test, in lowercase. The specification leaves the list
#                open; closing it keeps a typo ("feature:", "fixes:") from
#                passing for a type.
#   (scope)      optional: lowercase letters, digits, and . _ / -
#   !            optional: marks a breaking change
#   description  anything that is not empty. Its case is left free: Dependabot
#                writes "chore(deps): Bump ..." for NuGet and "ci(deps): bump
#                ..." for Actions, and both are fine.
#
# Plus the two subjects git writes on its own, so that reverting stays a
# one-click affair: Revert "..." and, when a revert is itself reverted,
# Reapply "...".

set -euo pipefail

# Byte-wise, whatever the locale of the machine: a range such as [a-z] then
# means the 26 ASCII letters, which it does not in every locale.
export LC_ALL=C

readonly types='build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test'
readonly conventional="^(${types})(\\([a-z0-9][a-z0-9._/-]*\\))?!?: [^[:space:]]"
readonly written_by_git='^(Revert|Reapply) ".+"$'

# What GitHub sends as "before" when a push creates the branch.
readonly no_commit='0000000000000000000000000000000000000000'

checked=0
failures=0
notes=0

# --- Workflow commands --------------------------------------------------------
#
# The runner reads its commands from what a step prints: a line starting with
# "::" (after spaces), and, in the older form, "##[command]" anywhere in a line
# (actions/runner, ActionCommand.TryParse searches the whole line). Printing a
# subject away from the start of a line is therefore not enough on its own.
# ::stop-commands:: makes the runner ignore both forms until it sees the token
# again; the token is random, so a title written in advance cannot guess it.

stop_token=""

stop_commands() {
  [ "${GITHUB_ACTIONS:-}" = "true" ] || return 0
  [ -z "$stop_token" ] || return 0

  stop_token=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
  [ "${#stop_token}" -eq 32 ] || { printf 'Cannot draw a random token from /dev/urandom.\n' >&2; exit 70; }

  printf '::stop-commands::%s\n' "$stop_token"
}

resume_commands() {
  [ -n "$stop_token" ] || return 0

  printf '::%s::\n' "$stop_token"
  stop_token=""
}

# Whatever happens, the commands of the steps that follow stay honoured.
trap resume_commands EXIT

usage_error() {
  printf '%s\n' "$1" >&2
  printf 'See: %s --help\n' "$0" >&2
  exit 64
}

# BASE_SHA and HEAD_SHA name commits: hexadecimal, nothing else. Checking it
# here keeps them from ever being read by git as an option.
require_object_name() {
  [[ "$2" =~ ^[0-9a-f]{7,64}$ ]] || usage_error "$1 must be a commit hash."
}

conforms() {
  [[ "$1" =~ $conventional ]] || [[ "$1" =~ $written_by_git ]]
}

# Prints one verdict. A subject is never printed at the start of a line, and
# its control characters are replaced, so that it cannot forge a line of its
# own either; in GitHub Actions, the commands are stopped around it as well.
#
#   check error|note <what> <subject>
check() {
  local level="$1" what="$2" subject="$3"
  local shown="${subject//[[:cntrl:]]/?}"

  checked=$((checked + 1))

  if conforms "$subject"; then
    printf '  ok    %s: %s\n' "$what" "$shown"
  elif [ "$level" = error ]; then
    printf '  FAIL  %s: %s\n' "$what" "$shown"
    failures=$((failures + 1))
  else
    printf '  note  %s: %s\n' "$what" "$shown"
    notes=$((notes + 1))
  fi
}

# Lists "<short hash> <subject>" lines. The callers pass --end-of-options: a
# range is data, never an option, even if it were to start with a dash. Git's
# own messages go straight to stderr, where they cannot be taken for a commit.
list() {
  local log

  if ! log=$(git log --format='%h %s' "$@"); then
    printf 'Cannot list the commits of %s.\n' "${*: -1}" >&2
    printf 'In CI, the checkout needs the whole history: fetch-depth: 0.\n' >&2
    exit 65
  fi

  printf '%s' "$log"
}

#   check_commits error|note <git log arguments...>
check_commits() {
  local level="$1"
  shift

  local log line
  log=$(list "$@")

  if [ -z "$log" ]; then
    printf '  (no commit to check other than merges)\n'
    return
  fi

  while IFS= read -r line; do
    [ -n "$line" ] || continue
    check "$level" "commit ${line%% *}" "${line#* }"
  done <<< "$log"
}

#   count_commits <range> [--no-merges]
count_commits() {
  local range="$1"
  shift

  if ! git rev-list --count "$@" --end-of-options "$range"; then
    printf 'Cannot count the commits of %s.\n' "$range" >&2
    printf 'In CI, the checkout needs the whole history: fetch-depth: 0.\n' >&2
    exit 65
  fi
}

# The base to compare a pull request with. In CI the checkout is GitHub's test
# merge of the head into the base: when HEAD is that merge, its first parent is
# the base as GitHub merged it, and the commits of the pull request are exactly
# the ones between it and the head. BASE_SHA, recorded in the event, is kept
# for any other checkout.
pull_request_base() {
  local base="$1" head="$2" second

  if second=$(git rev-parse -q --verify 'HEAD^2' 2>/dev/null) && [ "$second" = "$(git rev-parse -q --verify "$head^{commit}" 2>/dev/null)" ]; then
    git rev-parse 'HEAD^1'
  else
    printf '%s\n' "$base"
  fi
}

check_pull_request() {
  local title="$1" base="$2" head="$3"

  [ -n "$title" ] || usage_error "PR_TITLE is empty: a pull request always has a title."
  require_object_name BASE_SHA "$base"
  require_object_name HEAD_SHA "$head"

  base=$(pull_request_base "$base" "$head")

  printf 'Pull request title (the squash takes it when there are several commits):\n'
  check error "title" "$title"

  local total own
  total=$(count_commits "$base..$head")
  own=$(count_commits "$base..$head" --no-merges)

  if [ "$total" -eq 1 ]; then
    printf '\nIts only commit (the squash takes its subject):\n'
    check_commits error --max-count=1 --end-of-options "$head"
  elif [ "$own" -eq 1 ]; then
    printf '\nIts %d commits, one of them not a merge (the squash takes the title, or this subject if the merges are not counted):\n' "$total"
    check_commits error --no-merges --end-of-options "$base..$head"
  elif [ "$total" -gt 1 ]; then
    printf '\nIts %d commits (merges left out; they would only count if the pull request were rebase-merged):\n' "$total"
    check_commits note --no-merges --end-of-options "$base..$head"
  else
    printf '\n  (no commit between the base and the head)\n'
  fi
}

check_push() {
  local base="$1" head="$2"

  if [ -z "$head" ] || [ "$head" = "$no_commit" ]; then
    usage_error "HEAD_SHA is missing."
  fi

  require_object_name HEAD_SHA "$head"
  [ -z "$base" ] || require_object_name BASE_SHA "$base"

  if [ -z "$base" ] || [ "$base" = "$no_commit" ]; then
    # A branch created by this push has no "before" to compare with: its
    # latest commit is all there is to say — the latest that is not a merge,
    # since a branch assembled locally often ends with one.
    printf 'Latest commit of %s (a new branch):\n' "$head"
    check_commits error --no-merges --max-count=1 --end-of-options "$head"
  else
    printf 'Commits %s..%s (they have landed):\n' "$base" "$head"
    check_commits error --no-merges --end-of-options "$base..$head"
  fi
}

mode=environment
range=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --range)
      [ "$#" -ge 2 ] || usage_error "--range needs a value, such as origin/develop..HEAD"
      mode=range
      range="$2"
      shift 2
      ;;
    --stdin)
      mode=stdin
      shift
      ;;
    -h|--help)
      sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      usage_error "Unknown option: ${1//[[:cntrl:]]/?}"
      ;;
  esac
done

stop_commands

case "$mode" in
  range)
    printf 'Commits in %s:\n' "$range"
    check_commits error --no-merges --end-of-options "$range"
    ;;
  stdin)
    printf 'Subjects read from the standard input:\n'
    while IFS= read -r subject || [ -n "$subject" ]; do
      # Blank lines separate; an empty title is checked through PR_TITLE.
      [ -n "$subject" ] || continue
      check error "subject" "$subject"
    done
    ;;
  environment)
    event="${EVENT_NAME:-}"
    title="${PR_TITLE:-}"
    base="${BASE_SHA:-}"
    head="${HEAD_SHA:-}"

    case "$event" in
      pull_request)
        check_pull_request "$title" "$base" "$head"
        ;;
      push)
        check_push "$base" "$head"
        ;;
      "")
        usage_error "EVENT_NAME is not set (pull_request or push); outside CI, use --range or --stdin."
        ;;
      *)
        usage_error "Unexpected EVENT_NAME: ${event//[[:cntrl:]]/?} (pull_request or push)."
        ;;
    esac
    ;;
esac

# Nothing written by anyone else is printed from here on: the annotations
# below are this script's own, and need the commands back.
resume_commands

if [ "$notes" -gt 0 ]; then
  printf '\n%d commit subject(s) above do not follow Conventional Commits. A squash\n' "$notes"
  printf 'merge leaves them in the body only; a rebase merge would keep them.\n'

  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    printf '::warning title=Conventional commits::%d commit subject(s) do not follow Conventional Commits; fine for a squash merge, not for a rebase merge.\n' "$notes"
  fi
fi

if [ "$failures" -gt 0 ]; then
  cat <<EOF

$failures of $checked do not follow Conventional Commits.

Expected, in English:  <type>(<scope>)!: <description>
  type   build, chore, ci, docs, feat, fix, perf, refactor, revert, style or test
  scope  optional, lowercase: feat(audio): ...
  !      optional, marks a breaking change: feat!: ...

For example:
  feat(input): read the keyboard through evdev
  fix: restore the clipboard when the paste fails
  chore(deps): Bump the tests group with 1 update

A pull request title is fixed on GitHub, and this check runs again by itself.
A commit subject is fixed with "git commit --amend" (or "git rebase -i" and
"reword"), then a force push of the branch.
EOF

  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    printf '::error title=Conventional commits::%d of %d do not follow Conventional Commits, see the log.\n' "$failures" "$checked"
  fi

  exit 1
fi

printf '\nConventional Commits: %d checked, nothing blocking.\n' "$checked"
