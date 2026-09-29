#!/usr/bin/env bash
#
# Applies the rules kept in .github/rulesets/, and the repository settings
# that go with them, to the repository on GitHub.
#
#   scripts/apply-rulesets.sh --repo legb78/hex_linux           # dry run
#   scripts/apply-rulesets.sh --repo legb78/hex_linux --apply   # does it
#
# Without --apply nothing is written: the script reads the current state, when
# it can, and prints every call it would make. Without --repo it takes the
# repository of the current checkout (gh repo view).
#
# Requires gh, logged in with admin rights on the repository.

# What it applies, in this order:
#   1. the rulesets "Protect main", "Protect develop" and "Protect release
#      tags", from their JSON files: created when no ruleset of that name
#      exists, replaced when one does — so a second run changes nothing;
#   2. the merge settings: squash, merge commit and rebase allowed (the linear
#      history rule refuses merge commits on both branches anyway), head
#      branches deleted once merged, auto-merge off, and the squash commit
#      titled after the commit or the pull request (COMMIT_OR_PR_TITLE) with
#      the commit messages as its body — the settings of the Windows sibling;
#   3. GitHub Actions: the default token read-only and unable to approve pull
#      requests, and workflows of first-time contributors' forks waiting for
#      approval — the Windows sibling's values, set here rather than assumed.
#      all_external_contributors would be the stricter choice; the token of a
#      fork's pull request is read-only anyway, so what is at stake is runner
#      time, not the repository;
#   4. private vulnerability reporting, the channel SECURITY.md sends
#      reporters to. The Windows sibling points there too while having it off,
#      so a reporter following its policy finds no form: here it is part of
#      the setup, not left to memory;
#   5. secret scanning and its push protection, free on a public repository
#      (off on the Windows sibling);
#   6. Dependabot alerts, then Dependabot security updates, which need them.
#
# Left alone on purpose: "require actions pinned to a full SHA". The workflows
# reference actions/* by major tag, a trade-off explained in release.yml;
# turning the policy on would stop every run.
#
# Why rules in files and a script, rather than clicks in the settings: the
# rules can be reviewed in a pull request, compared with the CI job names they
# require, and applied again identically. They were written from the Windows
# sibling's rulesets as its API returns them, plus the second required check,
# the pinning of both checks to GitHub Actions (integration 15368: no other
# app can satisfy them by posting a status of the same name) and the tag rule.
#
# What "no bypass" protects, and what it does not. Nobody, the owner included,
# can push to main or develop directly, merge without green checks, or move or
# delete a release tag. That guards against mistakes and against other
# collaborators — not against the owner's own account or token being stolen:
# an administrator can edit or switch off any ruleset, and a token with the
# repo scope is an administrator's. Two-factor authentication and short-lived,
# fine-grained tokens are what protect that end.
#
# Run it once main and develop exist on GitHub. The rules require the status
# checks even when a branch is created (do_not_enforce_on_create is false, as
# on the Windows sibling), so a branch pushed after them could not be created
# at all. The script checks this before writing anything.

set -euo pipefail

apply=0
repo=""

usage_error() {
  printf '%s\n' "$1" >&2
  printf 'See: %s --help\n' "$0" >&2
  exit 64
}

die() {
  printf 'Error: %s\n' "$1" >&2
  exit 1
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --apply)
      apply=1
      shift
      ;;
    --dry-run)
      apply=0
      shift
      ;;
    --repo)
      [ "$#" -ge 2 ] || usage_error "--repo needs OWNER/NAME"
      repo="$2"
      shift 2
      ;;
    -h|--help)
      sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      usage_error "Unknown option: $1"
      ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ruleset_files=(
  "$root/.github/rulesets/protect-main.json"
  "$root/.github/rulesets/protect-develop.json"
  "$root/.github/rulesets/protect-tags.json"
)

# The settings of step 2, as gh api fields: -F sends true and false as
# booleans, -f sends a string as it is.
merge_fields=(
  -F allow_squash_merge=true
  -F allow_merge_commit=true
  -F allow_rebase_merge=true
  -F allow_auto_merge=false
  -F delete_branch_on_merge=true
  -f squash_merge_commit_title=COMMIT_OR_PR_TITLE
  -f squash_merge_commit_message=COMMIT_MESSAGES
)

# Step 5, as nested fields: key[subkey]=value builds {"key":{"subkey":...}}.
secret_scanning_fields=(
  -f 'security_and_analysis[secret_scanning][status]=enabled'
  -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled'
)

# Every write goes through here, and only here: in a dry run it is printed,
# quoted exactly as it would run, and nothing else happens.
write() {
  if [ "$apply" -eq 1 ]; then
    printf '  running: gh'
    printf ' %q' "$@"
    printf '\n'
    gh "$@"
  else
    printf '  would run: gh'
    printf ' %q' "$@"
    printf '\n'
  fi
}

# A read for the "now:" lines. Only called when the repository is reachable;
# a value that cannot be read is shown as such rather than stopping the run.
read_value() {
  gh api "$@" 2>/dev/null || printf 'unknown'
}

# The ruleset name, read from its file. The files are ours and formatted
# alike; the check keeps anything unexpected out of the jq filter below.
ruleset_name() {
  local name
  name=$(grep -m1 -oE '"name"[[:space:]]*:[[:space:]]*"[^"]*"' "$1" | sed -E 's/.*"([^"]*)"$/\1/')

  [[ "$name" =~ ^[A-Za-z0-9\ _.-]+$ ]] || die "unexpected ruleset name in $1: '$name'"
  printf '%s' "$name"
}

# --- Local checks, before anything touches the network --------------------

for file in "${ruleset_files[@]}"; do
  [ -f "$file" ] || die "missing $file"

  if command -v python3 >/dev/null 2>&1; then
    python3 -m json.tool "$file" >/dev/null || die "$file is not valid JSON"
  fi
done

if [ "$apply" -eq 1 ]; then
  printf 'Mode: APPLY — the calls below change the repository.\n'
else
  printf 'Mode: dry run — nothing is written. Add --apply to do it.\n'
fi

# --- The repository, and whether it is ready --------------------------------

reachable=1

if ! command -v gh >/dev/null 2>&1; then
  [ "$apply" -eq 0 ] || die "gh (GitHub CLI) not found: https://cli.github.com"
  printf 'Note: gh (GitHub CLI) is not installed, so the current state cannot be read.\n'
  reachable=0
elif ! gh auth status >/dev/null 2>&1; then
  [ "$apply" -eq 0 ] || die "gh is not logged in: gh auth login"
  printf 'Note: gh is not logged in, so the current state cannot be read.\n'
  reachable=0
fi

if [ -z "$repo" ]; then
  if [ "$reachable" -eq 1 ] && repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null) && [ -n "$repo" ]; then
    :
  else
    usage_error "No --repo given, and the current checkout has no GitHub repository."
  fi
fi

[[ "$repo" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage_error "--repo must be OWNER/NAME, not '$repo'"
printf 'Repository: %s\n' "$repo"

problems=0

if [ "$reachable" -eq 1 ]; then
  if admin=$(gh api "repos/$repo" --jq '.permissions.admin' 2>/dev/null); then
    if [ "$admin" != "true" ]; then
      printf 'Problem: the logged-in account has no admin rights on %s.\n' "$repo"
      problems=$((problems + 1))
    fi

    for branch in main develop; do
      if ! gh api "repos/$repo/branches/$branch" --jq '.name' >/dev/null 2>&1; then
        printf 'Problem: branch %s does not exist on GitHub yet. Push it first: the rules would stop its creation.\n' "$branch"
        problems=$((problems + 1))
      fi
    done
  else
    printf 'Problem: %s cannot be read (not created yet, or no access).\n' "$repo"
    problems=$((problems + 1))
    reachable=0
  fi
fi

if [ "$problems" -gt 0 ]; then
  [ "$apply" -eq 0 ] || die "$problems problem(s) above: nothing was written."
  printf 'The dry run goes on, to show what would be done.\n'
fi

# --- 1. Rulesets ------------------------------------------------------------

printf '\n1. Rulesets\n'

for file in "${ruleset_files[@]}"; do
  name=$(ruleset_name "$file")
  relative="${file#"$root"/}"
  checks=$(grep -oE '"context"[[:space:]]*:[[:space:]]*"[^"]*"' "$file" | sed -E 's/.*"([^"]*)"$/"\1"/' | paste -sd ' ' - || true)
  printf '%s, from %s — required checks: %s\n' "$name" "$relative" "${checks:-none}"

  id=""
  if [ "$reachable" -eq 1 ]; then
    id=$(gh api "repos/$repo/rulesets?includes_parents=false" --paginate --jq ".[] | select(.name == \"$name\") | .id")
    [ "$(printf '%s\n' "$id" | grep -c .)" -le 1 ] || die "several rulesets are named '$name': remove the extra ones first"
  fi

  if [ -n "$id" ]; then
    printf '  exists (id %s): replaced by the file\n' "$id"
    write api --method PUT "repos/$repo/rulesets/$id" --input "$file" --silent
  else
    [ "$reachable" -eq 1 ] && printf '  absent: created\n'
    write api --method POST "repos/$repo/rulesets" --input "$file" --silent
  fi
done

# --- 2. Merge settings ------------------------------------------------------

printf '\n2. Merge settings\n'

if [ "$reachable" -eq 1 ]; then
  printf '  now: %s\n' "$(read_value "repos/$repo" --jq '"squash \(.allow_squash_merge), merge commit \(.allow_merge_commit), rebase \(.allow_rebase_merge), auto-merge \(.allow_auto_merge), delete branch on merge \(.delete_branch_on_merge), squash title \(.squash_merge_commit_title), squash message \(.squash_merge_commit_message)"')"
fi
write api --method PATCH "repos/$repo" "${merge_fields[@]}" --silent

# --- 3. GitHub Actions --------------------------------------------------------

printf '\n3. GitHub Actions: default token, fork pull requests\n'

if [ "$reachable" -eq 1 ]; then
  printf '  now: %s\n' "$(read_value "repos/$repo/actions/permissions/workflow" --jq '"default token \(.default_workflow_permissions), can approve pull requests \(.can_approve_pull_request_reviews)"')"
  printf '  now: fork pull requests need approval for %s\n' "$(read_value "repos/$repo/actions/permissions/fork-pr-contributor-approval" --jq '.approval_policy')"
fi
write api --method PUT "repos/$repo/actions/permissions/workflow" \
  -f default_workflow_permissions=read -F can_approve_pull_request_reviews=false --silent
write api --method PUT "repos/$repo/actions/permissions/fork-pr-contributor-approval" \
  -f approval_policy=first_time_contributors --silent

# --- 4. Private vulnerability reporting --------------------------------------

printf '\n4. Private vulnerability reporting\n'

if [ "$reachable" -eq 1 ]; then
  printf '  now: enabled %s\n' "$(read_value "repos/$repo/private-vulnerability-reporting" --jq '.enabled')"
fi
write api --method PUT "repos/$repo/private-vulnerability-reporting" --silent

# --- 5. Secret scanning ---------------------------------------------------------

printf '\n5. Secret scanning and push protection\n'

if [ "$reachable" -eq 1 ]; then
  printf '  now: %s\n' "$(read_value "repos/$repo" --jq '"secret scanning \(.security_and_analysis.secret_scanning.status // "unknown"), push protection \(.security_and_analysis.secret_scanning_push_protection.status // "unknown")"')"
fi
write api --method PATCH "repos/$repo" "${secret_scanning_fields[@]}" --silent

# --- 6. Dependabot ------------------------------------------------------------

printf '\n6. Dependabot alerts, then security updates\n'

if [ "$reachable" -eq 1 ]; then
  # Answers 204 when the alerts are on, 404 when they are off.
  if gh api "repos/$repo/vulnerability-alerts" --silent >/dev/null 2>&1; then
    printf '  now: alerts on\n'
  else
    printf '  now: alerts off\n'
  fi
  printf '  now: security updates %s\n' "$(gh api "repos/$repo/automated-security-fixes" --jq '.enabled' 2>/dev/null || printf 'off')"
fi
write api --method PUT "repos/$repo/vulnerability-alerts" --silent
write api --method PUT "repos/$repo/automated-security-fixes" --silent

# --- Read back ----------------------------------------------------------------

if [ "$apply" -eq 1 ]; then
  printf '\nAs GitHub now reports it:\n'
  gh api "repos/$repo/rulesets?includes_parents=false" --paginate \
    --jq '.[] | "  ruleset \(.id): \(.name), \(.target), \(.enforcement)"'
  printf '  %s\n' "$(read_value "repos/$repo" --jq '"squash \(.allow_squash_merge), merge commit \(.allow_merge_commit), rebase \(.allow_rebase_merge), auto-merge \(.allow_auto_merge), delete branch on merge \(.delete_branch_on_merge), squash title \(.squash_merge_commit_title), squash message \(.squash_merge_commit_message)"')"
  printf '  %s\n' "$(read_value "repos/$repo/actions/permissions/workflow" --jq '"default token \(.default_workflow_permissions), can approve pull requests \(.can_approve_pull_request_reviews)"')"
  printf '  fork pull requests need approval for %s\n' "$(read_value "repos/$repo/actions/permissions/fork-pr-contributor-approval" --jq '.approval_policy')"
  printf '  private vulnerability reporting: %s\n' "$(read_value "repos/$repo/private-vulnerability-reporting" --jq '.enabled')"
  printf '  %s\n' "$(read_value "repos/$repo" --jq '"secret scanning \(.security_and_analysis.secret_scanning.status // "unknown"), push protection \(.security_and_analysis.secret_scanning_push_protection.status // "unknown")"')"
  printf '  Dependabot security updates: %s\n' "$(read_value "repos/$repo/automated-security-fixes" --jq '.enabled')"
  printf '\nThe Windows sibling'"'"'s pull_request rule also shows\n'
  printf 'require_extra_approval_for_unattributed_changes: true, a parameter the public API\n'
  printf 'description does not list as an input, so the files do not set it. Compare it on\n'
  printf 'both repositories (Settings > Rules > Rulesets) and align it by hand if needed.\n'
else
  printf '\nDry run finished: nothing was written.\n'
fi
