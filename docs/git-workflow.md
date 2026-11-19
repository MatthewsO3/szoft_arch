# Git workflow

How the team works on this repo. `main` is protected: nobody pushes to it
directly. All work happens on branches created from `main` and lands through a
reviewed pull request.

## Branch protection (set once on GitHub)

Configured at **Settings → Branches / Rules** on
`github.com/MatthewsO3/szoft_arch`. The rule on `main`:

- **Require a pull request before merging** — blocks direct pushes to `main`.
- **Require 1 approval** — at least one other team member reviews.
- **Require status checks to pass** — the CI `build-test` job
  (`.github/workflows/ci.yml`) must be green. (This option only appears after CI
  has run once, so open one PR first, then add the check.)
- **Do not allow bypassing / include administrators** — the rule applies to
  owners too.

## Day-to-day flow

```bash
# 1. Start from an up-to-date main
git checkout main
git pull

# 2. Create a feature branch from main
git checkout -b feature/short-description

# 3. Work, committing in small logical steps
git add -A
git commit -m "Clear, imperative message"

# 4. Push the branch and set upstream
git push -u origin feature/short-description

# 5. Open a pull request on GitHub targeting main.
#    CI runs automatically; request one review.

# 6. After approval and green CI, merge the PR on GitHub.
#    Then delete the branch (GitHub offers a button).

# 7. Back on your machine, sync main and remove the local branch
git checkout main
git pull
git branch -d feature/short-description
```

## Branch naming

Use a short prefix and a hyphenated description:

- `feature/…` — new functionality (`feature/ef-core-data-layer`)
- `fix/…` — bug fixes (`fix/answer-deadline-grace`)
- `docs/…` — documentation only (`docs/api-contract`)
- `chore/…` — tooling, CI, dependencies (`chore/pin-openapi`)

## Pull request expectations

- Keep PRs focused; one concern per PR is easier to review.
- Describe **what** changed and **why**. If the change touches the shared
  contract (SignalR methods/events or DTOs), say so and note that
  `QuizGame.Contracts`, `wwwroot/js/hubNames.js`, and `docs/api-contract.md`
  were updated together (see `.kiro/steering/structure.md`).
- CI must be green and the PR approved by at least one other member before merge.
- Everyone reviews at least one other member's PRs (plan §13).

## Commit messages

- First line: imperative mood, ~50 chars, no trailing period
  ("Add EF Core DbContext and first migration").
- Blank line, then a body explaining the reasoning when it isn't obvious.
- Reference the plan section or decision id when relevant (e.g. "plan §5", "D7").

## Keeping a branch current

If `main` moved while you worked, update your branch before merging:

```bash
git checkout main
git pull
git checkout feature/short-description
git merge main          # or: git rebase main (team preference)
# resolve conflicts, then push
```
