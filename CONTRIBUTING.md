# Contributing to HexLinux

Thanks for the interest. This is what to know before opening a pull request.

## Setting up

You need Linux and the **.NET 10 SDK**. WSL is enough to build and run the
unit tests; it is not enough to try the keyboard, see
[docs/testing.md](docs/testing.md).

On Debian or Ubuntu, one script installs the SDK in `~/.dotnet` and the tools
HexLinux drives at run time:

```sh
scripts/setup-dev.sh        # --no-apt for the SDK alone
scripts/get-model.sh        # recognition model, about 490 MB
dotnet build -c Release
dotnet test
```

On another distribution, install the .NET 10 SDK and the equivalent packages
by hand: the list, with what each one is for, is at the top of
`scripts/setup-dev.sh`.

## How the code is split, and why

The architecture separates two layers, for testability, exactly as the
Windows sibling of this project does.

**The shells** — `EvdevKeyboard`, `UinputKeyboard`, `AudioRecorder`,
`CueTones`, `ParakeetEngine`, `TextInjector`, `ControlSocket`, `TraySurface`
— wire up the system and decide nothing: the keyboards under `/dev/input`, the
virtual keyboard, the microphone and the tones through libpulse, the engine,
the clipboard and keystroke tools, the control socket, the tray and the
notifications on the session bus. They cannot be tested automatically — a CI
runner has no keyboard device, no microphone and no desktop session — so each
is marked `[ExcludeFromCodeCoverage(Justification = "...")]` and verified by
hand, through the diagnostic modes.

**The pure logic** — `ChordDetector`, `InputDeviceCatalog`, `RecordingGuards`,
`SpeechSegmenter`, `TranscriptCleaner`, `AppSettings`, `AppPaths`,
`ModelLocator`, `DesktopSession`, `LogindState`, `ControlCommands`,
`DesktopEntry`, `DictationCoordinator`, `IdlePolicy`, `FeedbackPolicy` and the
insertion planner — holds every decision and is tested without any of that.

> **If you add a decision, it belongs in the pure layer.** That is where the
> expensive bugs live: keyboard auto-repeat, keys released out of order, a key
> left held on a keyboard that was unplugged, a second dictation started while
> one is still transcribing, the wrong tool picked for a Wayland desktop. Each
> of those is painful to reproduce by hand and trivial to describe as a test.

## Tests

```sh
dotnet test                                  # everything
dotnet test --filter Category=Integration    # the engine ones only
```

The integration tests load the real engine. Without the model on disk they
**skip themselves with a message** rather than fail, so a fresh clone gives a
green run: nobody has to tell real failures apart from a missing 490 MB
download. Fetch the model and they run for real.

CI excludes them up front — a runner has no reason to spend minutes discovering
they would skip. To run exactly what CI runs, in the same order:

```sh
dotnet restore
dotnet build -c Release --no-restore
dotnet format --verify-no-changes --no-restore
dotnet test -c Release --no-build --filter "Category!=Integration" -p:CollectCoverage=true -p:ExcludeByAttribute=ExcludeFromCodeCoverage -p:Threshold=75 -p:ThresholdType=line -p:ThresholdStat=total
```

A test should explain **why** the case matters, not only what it checks. One
line recalling the real situation it covers is worth more than a long name.

## Verifying what cannot be tested

Any change touching the keyboard, the microphone, insertion, the clipboard,
the tray or the notifications needs a manual check, on a real desktop session.
The diagnostic modes exist for that:

```sh
./hexlinux --doctor                 # what does HexLinux see of this session?
./hexlinux --record test.wav        # is the microphone picking anything up?
./hexlinux --transcribe test.wav    # does the engine transcribe?
./hexlinux --watch-hotkey           # does the hotkey fire?
./hexlinux --inject "some text"     # does insertion land? (--mode Type, --sender ...)
./hexlinux --test-feedback          # do the start and end tones play?
./hexlinux --toggle                 # does the running daemon answer?
```

The session type changes the outcome as much as the desktop does: GNOME on
Wayland and GNOME on Xorg insert text in different ways. Say in the pull
request which distribution, desktop and session you tried, what you did and
what you observed. [docs/testing.md](docs/testing.md) has the per-layer
checklist, and the way to set up a virtual machine for it.

## Git flow

| Branch | Role |
|--------|------|
| `main` | Published releases. Only ever advances from `develop`. |
| `develop` | Integration. Work branches are merged here. |

Both are protected: no direct pushes, no force pushes, no deletion, and a pull
request with both checks green — `Build, tests and coverage` and
`Conventional commits` — is required. Release tags (`v*`) cannot be moved or
deleted. There is no bypass, for anyone.

What that protects, and what it does not: the owner cannot push to `main` by
mistake any more than a collaborator could, and a release tag keeps pointing at
what was published. It does not protect against the owner's own account or
token being stolen: an administrator can edit or switch off the rules, and a
`gh` token with the `repo` scope is an administrator's. Two-factor
authentication and short-lived, fine-grained tokens are what guard that end.

One branch per change, created from `develop` and merged back into it.

```sh
git checkout develop
git pull
git checkout -b feat/my-topic
gh pr create --base develop
```

Prefixes: `feat/`, `fix/`, `chore/`, `ci/`, `docs/`.

### Squash-merging develop into main: the duplicate commits

`main` advances from `develop` through a pull request, and both branches refuse
merge commits (linear history). What remains is a squash or a rebase merge —
and GitHub writes **new commits** for both: a squash turns the pull request
into one commit on `main`, and a rebase merge, as GitHub's own documentation
puts it, "always updates the committer information and creates new commit
SHAs". The same change then exists twice, once on each branch, and `develop`
never contains `main`'s commits.

The Windows sibling shows the effect. `chore: release 1.1.0 (#41)` exists as
`d5451ae` on `develop` and as `20ab04e` on `main`; the two branches report as
diverged (`develop` 18 commits ahead, 5 behind), and each release pull request
lists every commit since the branches split, those already released included
(15 for one sync, 20 for the next release).

It is cosmetic — the trees are identical, and nothing conflicts as long as
`main` only ever moves from `develop` — but it grows. Two ways to deal with it:

- **Keep it small** (the choice here, since these are the Windows sibling's
  rules). Cut a release branch from `develop`, title the pull request
  `chore: release X.Y.Z`, and squash it. With several commits the squash takes
  the pull request title; replace the generated body, which would list every
  commit since the split again, with a line or two of release notes.
- **Remove it**, by changing one rule: allow merge commits into `main` (drop
  `required_linear_history` from `.github/rulesets/protect-main.json` and keep
  only `merge` in its `allowed_merge_methods`) and merge release branches with a
  merge commit. `develop`'s commits then reach `main` as themselves, and each
  release pull request lists only what is new. The price: `main` is no longer
  linear (`git log --first-parent main` still reads one entry per release).
  Apply the change with `scripts/apply-rulesets.sh`.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/), in English.

```
feat(audio): capture the microphone through libpulse-simple

The body explains WHY, not what — the diff already says what. What was tried,
what failed, the constraint that forced this solution over another.
```

Accepted types: `build`, `chore`, `ci`, `docs`, `feat`, `fix`, `perf`,
`refactor`, `revert`, `style`, `test`, in lowercase; an optional lowercase
scope in parentheses; `!` before the colon for a breaking change. Git's own
`Revert "..."` and `Reapply "..."` subjects are accepted as they come.

The `Conventional commits` check enforces it on what a squash actually writes.
The repository takes the squash title from the commit when a pull request has
only one, and from the pull request title otherwise, so the check verifies:

- the pull request **title**, always;
- the subject of its commit, when it has **only one**;
- the subjects of a pull request with several commits are **reported** in the
  check's log, without failing it — a squash only lists them in the body. A
  rebase merge would keep them as they are: squash, or reword them first;
- merge commits written by the **Update branch** button are left out: the rules
  require a branch to be up to date before it merges, so they are routine.

After the merge, the same check runs on the push to `develop` and fails if a
subject that landed does not conform — which is where a squash title edited by
hand in the merge dialog would show.

Check a branch before opening its pull request:

```sh
scripts/check-conventional-commits.sh --range origin/develop..HEAD
```

A title is fixed on GitHub, and the check runs again by itself. A commit
subject is fixed with `git commit --amend` (or `git rebase -i` and `reword`),
then a force push of your branch.

## What the build enforces

`TreatWarningsAsErrors` is on: **a single warning fails the build**, locally and
in CI. This is not negotiable. It is what keeps the code clean without anyone
having to think about it.

CI also refuses a change that `dotnet format` would rewrite, a line coverage of
the pure logic below 75 %, a dependency with a known vulnerability (transitive
ones included), and a title or subject outside Conventional Commits.

## Releasing

`Directory.Build.props` holds `<Version>` and is the single source of truth.
Merging into `main` publishes that version and does nothing if the tag already
exists, so releasing means bumping the number in a pull request.

The release workflow re-runs the unit tests, builds the self-contained
executable, packs `hexlinux-linux-x64.tar.gz` with its `SHA256SUMS`, and
publishes both. The job that builds holds a read-only token; the job that
publishes holds the only write token and runs nothing of the project. A
published tag cannot be moved or deleted, so a broken release is fixed by the
next version, never by replacing the files of this one.

`scripts/publish.sh` builds the same self-contained executable locally.

### Setting up the repository, once

For whoever creates the GitHub repository:

1. Create it, then push `main` and `develop`. The rules must come after the
   branches: they require the status checks even when a branch is created.
2. Preview, then apply the rules and settings:

   ```sh
   scripts/apply-rulesets.sh --repo OWNER/NAME           # dry run: changes nothing
   scripts/apply-rulesets.sh --repo OWNER/NAME --apply
   ```

   It sets the three rulesets, the merge settings, a read-only default token
   for workflows, private vulnerability reporting, secret scanning with push
   protection, and Dependabot's security updates.
3. In Settings > Pages, publish the `docs/` folder of `develop`, as the Windows
   sibling does for its landing page.

## Licence

By contributing, you agree that your contribution is distributed under the
project's Apache 2.0 licence.
