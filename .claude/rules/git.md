# Git

- Never stage or commit: no `git add`, `git stage`, `git commit` (including `-a` / `--amend`).
  The user does all staging and commits. Leave changes unstaged in the working tree and just
  report what changed — don't offer to commit.
- Never create branches: no `git branch <name>`, `git checkout -b`, `git switch -c`, or
  worktrees. Work on whatever branch is currently checked out; the user manages branches.
- Read-only git commands (`status`, `diff`, `log`, `show`, `blame`) are fine.
