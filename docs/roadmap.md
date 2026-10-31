# Launcher roadmap

What to build next, from the first round of feedback on the launcher. Each item says where it stands, what it needs
from the server (a hook in the server repository, per [AGENTS.md](../AGENTS.md)) and what is still undecided.

To pick an item up, mark it **in progress** with your name or PR here; when it ships, describe it in
[launcher.md](launcher.md) and remove it from this file in the same change.

Order: branches and pull requests (built, being checked); the collaboration items can happen at any time.

## 1. Pick a branch or a pull request

**In progress**: built, waiting for a check on the real checkout. The Versions section ([launcher.md](launcher.md),
"Versions") lists origin's branches and the open pull requests and switches between them with the guards described
there. Checked: both lists against the real repository in both themes, and every switch against a throwaway repository
(branch, pull request, force-pushed pull request, refusal over a local commit, stash and restore, update). No server
change.

Left to do:

- On the real checkout: switch to a pull request (a fork's: the question once), Play it, Back to main with an
  uncommitted change (stashed and offered back), Update main, Clean up. Then this item leaves the roadmap.

## 2. Collaboration

- Decide with the LocalExile maintainers whether the repository moves to the LocalExile organisation. If it does, change
  the release lookup (`Updates.cs`, now `jetstream1128/LocalExile-Launcher`) and publish a release before relying on
  GitHub's redirect.
- `CONTRIBUTING.md`: the rules from [AGENTS.md](../AGENTS.md), how to test against a real checkout, and that releases
  come only from a maintainer's tag.
- Protect `main`: changes arrive through reviewed pull requests.

## 3. Ideas, not planned yet

- Server timings: message names instead of hex ids (the server has no complete id-to-name table yet), a reset button,
  CSV export for bug reports.
