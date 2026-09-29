#!/usr/bin/env bash
#
# Compares the repository on GitHub with the rules kept in .github/rulesets/
# and the settings that go with them, and applies them on request.
#
#   scripts/apply-rulesets.sh --repo legb78/hex_linux           # dry run: compares, writes nothing
#   scripts/apply-rulesets.sh --repo legb78/hex_linux --apply   # writes what differs
#
# Optional, off by default: suggestions, not the rules in force.
#   --require-conventional-commits   require the "Conventional commits" check too
#   --pin-checks-to-actions          let only GitHub Actions satisfy the checks
#   --with-tag-ruleset               protect the release tags (optional/protect-tags.json)
#   --enable-private-reporting       turn on private vulnerability reporting
#   --enable-secret-scanning         turn on secret scanning and push protection
#   --with-pages                     publish docs/ of develop with GitHub Pages
#
# Without --repo it takes the repository of the current checkout. Requires gh,
# logged in with admin rights on the repository, and python3.

# What the files hold. The rulesets in force on GitHub are the Windows
# sibling's, copied as they are — both repositories answered the same through
# the API on 2026-09-29: no bypass for anyone; a pull request to change main or
# develop, with no approval needed (a one-person project could not give one)
# but every conversation resolved; linear history; the branch up to date and
# "Build, tests and coverage" green; no deletion, no force push. The files
# restate exactly that, so a dry run on this repository reports no difference.
# GitHub adds two parameters of its own to the pull request rule when it
# returns it (required_reviewers, require_extra_approval_for_unattributed_changes):
# the files do not set them, and the comparison lists them apart.
#
# What a run without options applies — the Windows sibling's settings, as its
# API answered them on 2026-09-29:
#   1. the rulesets, created when no ruleset of that name exists, replaced when
#      one does and differs — so a second run changes nothing;
#   2. the merge settings: squash, merge commit and rebase allowed (the linear
#      history rule refuses merge commits on both branches anyway), head
#      branches deleted once merged, auto-merge off, the squash commit titled
#      after the commit or the pull request (COMMIT_OR_PR_TITLE) with the commit
#      messages as its body;
#   3. GitHub Actions: the default token read-only and unable to approve pull
#      requests, and workflows from first-time contributors' forks waiting for
#      approval. all_external_contributors would be the stricter choice; the
#      token of a fork's pull request is read-only anyway, so what is at stake
#      is runner time, not the repository. (GitHub refuses that setting on a
#      private repository: the script then leaves it out.);
#   4. Dependabot alerts, then Dependabot security updates, which need them —
#      on in the Windows sibling.
#
# The options, which nobody has decided to apply:
#   --require-conventional-commits  CI runs that job on every pull request; it
#       is a warning today, and this makes it a lock. The squash takes its title
#       from what the job checks, so the lock would enforce the commit
#       convention rather than hope for it.
#   --pin-checks-to-actions  adds "integration_id": 15368 (the GitHub Actions
#       app, as GET /apps/github-actions answers) to each required check: no
#       other app could then satisfy it by posting a status of the same name.
#   --with-tag-ruleset  forbids deleting, moving or re-creating a v* tag once it
#       is published. Without it, the release workflow never moves a tag, but
#       the rules do not stop an administrator from doing so.
#   --enable-private-reporting  the "Report a vulnerability" form SECURITY.md
#       sends reporters to. Off in the Windows sibling, whose SECURITY.md points
#       at the same form: until it is on, reporters find no button there, and
#       SECURITY.md gives them a way around. Public repositories only
#       ("Owners and administrators of public repositories can allow security
#       researchers to report vulnerabilities...", GitHub's documentation).
#   --enable-secret-scanning  secret scanning and its push protection, free on
#       a public repository ("Secret scanning runs automatically for free").
#       Off in the Windows sibling.
#   --with-pages  the landing page, docs/ of develop, as the Windows sibling
#       publishes its own. Worth it once docs/index.html has reached develop.
#
# Left alone on purpose: "require actions pinned to a full SHA". The workflows
# reference actions/* by major tag, a trade-off explained in release.yml;
# turning the policy on would stop every run.
#
# What "no bypass" protects, and what it does not. Nobody, the owner included,
# can push to main or develop directly or merge without the green check. That
# guards against mistakes and against other collaborators — not against the
# owner's own account or token being stolen: an administrator can edit or
# switch off any ruleset, and a token with the repo scope is an
# administrator's. Two-factor authentication and short-lived, fine-grained
# tokens are what protect that end.
#
# Rulesets come after the branches: they require the status check even when a
# branch is created (do_not_enforce_on_create is false, as on the Windows
# sibling), so a branch pushed after them could not be created at all. The
# script checks that main and develop exist before writing anything.

set -euo pipefail

apply=0
repo=""
require_conventional=0
pin_checks=0
with_tags=0
enable_reporting=0
enable_scanning=0
with_pages=0

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
    --apply) apply=1; shift ;;
    --dry-run) apply=0; shift ;;
    --repo)
      [ "$#" -ge 2 ] || usage_error "--repo needs OWNER/NAME"
      repo="$2"
      shift 2
      ;;
    --require-conventional-commits) require_conventional=1; shift ;;
    --pin-checks-to-actions) pin_checks=1; shift ;;
    --with-tag-ruleset) with_tags=1; shift ;;
    --enable-private-reporting) enable_reporting=1; shift ;;
    --enable-secret-scanning) enable_scanning=1; shift ;;
    --with-pages) with_pages=1; shift ;;
    -h|--help)
      sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) usage_error "Unknown option: $1" ;;
  esac
done

command -v python3 >/dev/null 2>&1 || die "python3 is needed to read and compare the rulesets."

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ruleset_files=(
  "$root/.github/rulesets/protect-main.json"
  "$root/.github/rulesets/protect-develop.json"
)
if [ "$with_tags" -eq 1 ]; then
  ruleset_files+=("$root/.github/rulesets/optional/protect-tags.json")
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

# The JSON work, in one place: preparing a ruleset (the file, plus the
# optional changes asked for) and comparing it with what GitHub returns.
#
#   rulesets prepare <file> <out> <require-conventional 0|1> <pin 0|1>
#   rulesets name <file>
#   rulesets checks <file>
#   rulesets compare <wanted> <live>     exit 0 same, 1 different
rulesets() {
  python3 - "$@" <<'PY'
import json
import sys

mode = sys.argv[1]

def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)

if mode == "prepare":
    doc = load(sys.argv[2])
    require_conventional = sys.argv[4] == "1"
    pin = sys.argv[5] == "1"
    for rule in doc.get("rules", []):
        if rule.get("type") != "required_status_checks":
            continue
        checks = rule["parameters"]["required_status_checks"]
        if require_conventional and not any(c.get("context") == "Conventional commits" for c in checks):
            checks.append({"context": "Conventional commits"})
        if pin:
            for check in checks:
                check["integration_id"] = 15368
    with open(sys.argv[3], "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)
    sys.exit(0)

if mode == "name":
    print(load(sys.argv[2])["name"])
    sys.exit(0)

if mode == "checks":
    doc = load(sys.argv[2])
    names = [c["context"] for r in doc.get("rules", []) if r.get("type") == "required_status_checks"
             for c in r["parameters"]["required_status_checks"]]
    print(", ".join('"%s"' % n for n in names) or "none")
    sys.exit(0)

# compare: every value the file sets must be what GitHub has; what GitHub has
# beyond the file is listed inside the rules only (the rest is metadata: id,
# links, dates).
wanted, live = load(sys.argv[2]), load(sys.argv[3])
different, github_only = [], []

def by_type(rules):
    return {r.get("type"): r for r in rules}

def same_list(a, b):
    key = lambda item: json.dumps(item, sort_keys=True)
    return sorted(a, key=key) == sorted(b, key=key)

def walk(want, have, path, inside_rules):
    if isinstance(want, dict) and isinstance(have, dict):
        for k, v in want.items():
            if k not in have:
                different.append(f"{path}.{k}: not on GitHub (file: {json.dumps(v)})")
            else:
                walk(v, have[k], f"{path}.{k}", inside_rules or k == "rules")
        if inside_rules:
            for k in have:
                if k not in want:
                    github_only.append(f"{path}.{k} = {json.dumps(have[k])}")
    elif path.endswith(".rules") and isinstance(want, list) and isinstance(have, list):
        w, h = by_type(want), by_type(have)
        for t in w:
            if t not in h:
                different.append(f"rule {t}: not on GitHub")
            else:
                walk(w[t], h[t], f"rule {t}", True)
        for t in h:
            if t not in w:
                different.append(f"rule {t}: on GitHub, not in the file")
    elif isinstance(want, list) and isinstance(have, list):
        if not same_list(want, have):
            different.append(f"{path}: file {json.dumps(want)}, GitHub {json.dumps(have)}")
    elif want != have:
        different.append(f"{path}: file {json.dumps(want)}, GitHub {json.dumps(have)}")

walk(wanted, live, "", False)
for line in different:
    print("  differs: " + line.lstrip("."))
for line in github_only:
    print("  set by GitHub, not by the file: " + line.lstrip("."))
if not different:
    print("  matches the file")
sys.exit(1 if different else 0)
PY
}

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

# A value read from the API, or "unknown". On an error gh prints the error
# body on the standard output, which must not pass for the value.
read_value() {
  local value
  if value=$(gh api "$@" 2>/dev/null); then
    printf '%s' "$value"
  else
    printf 'unknown'
  fi
}

# --- Local checks, before anything touches the network --------------------

for file in "${ruleset_files[@]}"; do
  [ -f "$file" ] || die "missing $file"
  python3 -m json.tool "$file" >/dev/null || die "$file is not valid JSON"
done

if [ "$apply" -eq 1 ]; then
  printf 'Mode: APPLY — the calls below change the repository.\n'
else
  printf 'Mode: dry run — nothing is written. Add --apply to do it.\n'
fi

options=""
[ "$require_conventional" -eq 0 ] || options+=" --require-conventional-commits"
[ "$pin_checks" -eq 0 ] || options+=" --pin-checks-to-actions"
[ "$with_tags" -eq 0 ] || options+=" --with-tag-ruleset"
[ "$enable_reporting" -eq 0 ] || options+=" --enable-private-reporting"
[ "$enable_scanning" -eq 0 ] || options+=" --enable-secret-scanning"
[ "$with_pages" -eq 0 ] || options+=" --with-pages"
printf 'Options:%s\n' "${options:- none (the rules and settings of the Windows sibling)}"

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
visibility=unknown

if [ "$reachable" -eq 1 ]; then
  if facts=$(gh api "repos/$repo" --jq '"\(.permissions.admin) \(.private)"' 2>/dev/null); then
    [ "${facts% *}" = "true" ] || { printf 'Problem: the logged-in account has no admin rights on %s.\n' "$repo"; problems=$((problems + 1)); }
    if [ "${facts#* }" = "true" ]; then visibility=private; else visibility=public; fi

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

printf 'Visibility: %s\n' "$visibility"

if [ "$problems" -gt 0 ]; then
  [ "$apply" -eq 0 ] || die "$problems problem(s) above: nothing was written."
  printf 'The dry run goes on, to show what would be done.\n'
fi

# --- 1. Rulesets ------------------------------------------------------------

printf '\n1. Rulesets\n'

for file in "${ruleset_files[@]}"; do
  wanted="$work/$(basename "$file")"
  rulesets prepare "$file" "$wanted" "$require_conventional" "$pin_checks"
  name=$(rulesets name "$wanted")
  [[ "$name" =~ ^[A-Za-z0-9\ _.-]+$ ]] || die "unexpected ruleset name in $file: '$name'"
  printf '%s, from %s — required checks: %s\n' "$name" "${file#"$root"/}" "$(rulesets checks "$wanted")"

  id=""
  if [ "$reachable" -eq 1 ]; then
    id=$(gh api "repos/$repo/rulesets?includes_parents=false" --paginate --jq ".[] | select(.name == \"$name\") | .id")
    [ "$(printf '%s\n' "$id" | grep -c .)" -le 1 ] || die "several rulesets are named '$name': remove the extra ones first"
  fi

  if [ -n "$id" ]; then
    gh api "repos/$repo/rulesets/$id" > "$work/live.json"
    if rulesets compare "$wanted" "$work/live.json"; then
      printf '  (id %s) nothing to do\n' "$id"
    else
      write api --method PUT "repos/$repo/rulesets/$id" --input "$wanted" --silent
    fi
  else
    [ "$reachable" -eq 0 ] || printf '  absent: created\n'
    write api --method POST "repos/$repo/rulesets" --input "$wanted" --silent
  fi
done

# --- 2. Merge settings ------------------------------------------------------

printf '\n2. Merge settings\n'

merge_wanted=(
  allow_squash_merge=true
  allow_merge_commit=true
  allow_rebase_merge=true
  allow_auto_merge=false
  allow_update_branch=false
  delete_branch_on_merge=true
  squash_merge_commit_title=COMMIT_OR_PR_TITLE
  squash_merge_commit_message=COMMIT_MESSAGES
  merge_commit_title=MERGE_MESSAGE
  merge_commit_message=PR_TITLE
)

merge_fields=()
merge_differs=0
for pair in "${merge_wanted[@]}"; do
  key="${pair%%=*}"
  value="${pair#*=}"
  # -F sends true and false as booleans, -f sends a string as it is.
  case "$value" in
    true|false) merge_fields+=(-F "$pair") ;;
    *) merge_fields+=(-f "$pair") ;;
  esac

  if [ "$reachable" -eq 1 ]; then
    now=$(read_value "repos/$repo" --jq ".$key")
    if [ "$now" != "$value" ]; then
      printf '  %s: %s, wanted %s\n' "$key" "$now" "$value"
      merge_differs=1
    fi
  fi
done

if [ "$reachable" -eq 1 ] && [ "$merge_differs" -eq 0 ]; then
  printf '  as wanted, nothing to do\n'
else
  write api --method PATCH "repos/$repo" "${merge_fields[@]}" --silent
fi

# --- 3. GitHub Actions --------------------------------------------------------

printf '\n3. GitHub Actions: default token, fork pull requests\n'

token_now="unknown"
if [ "$reachable" -eq 1 ]; then
  token_now=$(read_value "repos/$repo/actions/permissions/workflow" --jq '"\(.default_workflow_permissions) \(.can_approve_pull_request_reviews)"')
  printf '  now: default token %s\n' "$token_now"
fi
if [ "$token_now" = "read false" ]; then
  printf '  read-only and unable to approve pull requests, nothing to do\n'
else
  write api --method PUT "repos/$repo/actions/permissions/workflow" \
    -f default_workflow_permissions=read -F can_approve_pull_request_reviews=false --silent
fi

if [ "$visibility" = private ]; then
  printf '  fork pull requests: left out, GitHub refuses the setting on a private repository\n'
else
  fork_now=unknown
  if [ "$reachable" -eq 1 ]; then
    fork_now=$(read_value "repos/$repo/actions/permissions/fork-pr-contributor-approval" --jq '.approval_policy')
    printf '  now: fork pull requests need approval for %s\n' "$fork_now"
  fi
  if [ "$fork_now" = first_time_contributors ]; then
    printf '  as wanted, nothing to do\n'
  else
    write api --method PUT "repos/$repo/actions/permissions/fork-pr-contributor-approval" \
      -f approval_policy=first_time_contributors --silent
  fi
fi

# --- 4. Dependabot ------------------------------------------------------------

printf '\n4. Dependabot alerts, then security updates\n'

alerts=unknown
updates=unknown
if [ "$reachable" -eq 1 ]; then
  # Answers 204 when the alerts are on, 404 when they are off.
  if gh api "repos/$repo/vulnerability-alerts" --silent >/dev/null 2>&1; then alerts=on; else alerts=off; fi
  updates=$(read_value "repos/$repo/automated-security-fixes" --jq 'if .enabled then "on" else "off" end')
  printf '  now: alerts %s, security updates %s\n' "$alerts" "$updates"
fi
[ "$alerts" = on ] || write api --method PUT "repos/$repo/vulnerability-alerts" --silent
[ "$updates" = on ] || write api --method PUT "repos/$repo/automated-security-fixes" --silent
if [ "$alerts" = on ] && [ "$updates" = on ]; then
  printf '  both on, nothing to do\n'
fi

# --- 5. Private vulnerability reporting (option) ------------------------------

printf '\n5. Private vulnerability reporting (--enable-private-reporting)\n'

reporting_now=unknown
if [ "$reachable" -eq 1 ] && [ "$visibility" = public ]; then
  reporting_now=$(read_value "repos/$repo/private-vulnerability-reporting" --jq '.enabled')
  printf '  now: enabled %s\n' "$reporting_now"
fi

if [ "$visibility" = private ]; then
  printf '  not available: GitHub offers it on public repositories only\n'
elif [ "$reporting_now" = true ]; then
  printf '  on, nothing to do\n'
elif [ "$enable_reporting" -eq 1 ]; then
  write api --method PUT "repos/$repo/private-vulnerability-reporting" --silent
else
  printf '  left as it is (off in the Windows sibling); the option turns it on\n'
fi

# --- 6. Secret scanning (option) ----------------------------------------------

printf '\n6. Secret scanning and push protection (--enable-secret-scanning)\n'

scanning_now=unknown
if [ "$reachable" -eq 1 ]; then
  scanning_now=$(read_value "repos/$repo" --jq '"\(.security_and_analysis.secret_scanning.status // "unknown") \(.security_and_analysis.secret_scanning_push_protection.status // "unknown")"')
  printf '  now: secret scanning, then push protection: %s\n' "$scanning_now"
fi

if [ "$scanning_now" = "enabled enabled" ]; then
  printf '  both on, nothing to do\n'
elif [ "$enable_scanning" -eq 1 ]; then
  if [ "$visibility" = private ]; then
    printf '  a private repository needs GitHub Secret Protection for this; GitHub may refuse it\n'
  fi
  write api --method PATCH "repos/$repo" \
    -f 'security_and_analysis[secret_scanning][status]=enabled' \
    -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled' --silent
else
  printf '  left as it is (off in the Windows sibling); the option turns both on\n'
fi

# --- 7. GitHub Pages (option) -------------------------------------------------

printf '\n7. GitHub Pages: docs/ of develop (--with-pages)\n'

pages_now=unknown
if [ "$reachable" -eq 1 ]; then
  # 404 when the repository has no Pages site; gh then prints the error body
  # on the standard output, hence the separate probe.
  if gh api "repos/$repo/pages" --silent >/dev/null 2>&1; then
    pages_now=$(read_value "repos/$repo/pages" --jq '"\(.source.branch):\(.source.path)"')
  else
    pages_now=none
  fi
  printf '  now: %s\n' "$pages_now"
fi

if [ "$pages_now" = "develop:/docs" ]; then
  printf '  as wanted, nothing to do\n'
elif [ "$with_pages" -eq 0 ]; then
  printf '  left as it is; the option publishes docs/ of develop, as the Windows sibling does\n'
elif [ "$pages_now" = none ]; then
  write api --method POST "repos/$repo/pages" \
    -f build_type=legacy -f 'source[branch]=develop' -f 'source[path]=/docs' --silent
else
  write api --method PUT "repos/$repo/pages" \
    -f build_type=legacy -f 'source[branch]=develop' -f 'source[path]=/docs' --silent
fi

# --- End ----------------------------------------------------------------------

if [ "$apply" -eq 1 ]; then
  printf '\nDone. Run the script again without --apply: it should report nothing to do.\n'
else
  printf '\nDry run finished: nothing was written.\n'
fi
